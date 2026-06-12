using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocKit.App.Core;

namespace LocKit.App
{
    public class TranslationRow : INotifyPropertyChanged
    {
        private string _translation = string.Empty;

        public int Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Original { get; set; } = string.Empty;

        public string Translation
        {
            get => _translation;
            set
            {
                if (_translation != value)
                {
                    _translation = value;
                    OnPropertyChanged(nameof(Translation));
                }
            }
        }

        public Dictionary<string, string> CustomColumns { get; set; } = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public partial class MainWindow : Window
    {
        private readonly DbService _dbService = new();
        private readonly DecompilerService _decompilerService = new();
        private readonly LlmService _llmService = new();
        private ObservableCollection<TranslationRow> _translationItems = new();
        private ObservableCollection<FileItemViewModel> _fileItems = new();
        private Dictionary<string, bool> _fileCheckedSnapshot = new();
        private bool _isAiOpen = true;
        private int _customColumnCounter = 1;
        private TranslationRow? _selectedRow;
        private string _lastGameFolder = string.Empty;

        public MainWindow() : this(null)
        {
        }

        public MainWindow(string? initialProjectPath = null)
        {
            InitializeComponent();
            
            // Initialize global database first
            InitializeGlobalDatabase();

            string? projectToLoad = initialProjectPath;
            if (string.IsNullOrEmpty(projectToLoad))
            {
                projectToLoad = _dbService.GetSetting("last_project_path", "", isGlobal: true);
            }

            if (!string.IsNullOrEmpty(projectToLoad) && File.Exists(projectToLoad))
            {
                _dbService.SetDatabasePath(projectToLoad);
                _dbService.InitializeDatabase(seedDemo: false);
                Title = $"LocKit - {Path.GetFileName(projectToLoad)}";
                _lastGameFolder = _dbService.GetSetting("game_folder_path", "");
            }
            else
            {
                _dbService.SetDatabasePath("lockit.db");
                _dbService.InitializeDatabase(seedDemo: true);
            }

            SetupDataFromDb();
            
            // Auto-verify FFI connection at startup
            VerifyRustFFI();

            LoadLlmSettings();

            InitializeGridContextMenu();
        }

        private void InitializeGlobalDatabase()
        {
            try
            {
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=lockit.db");
                connection.Open();
                using var cmd = new Microsoft.Data.Sqlite.SqliteCommand("CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT);", connection);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init global DB: {ex.Message}");
            }
        }

        private void SetupDataFromDb()
        {
            var files = _dbService.GetFiles();
            var items = files.Select(f => new FileItemViewModel { Name = f, IsChecked = true }).ToList();
            _fileItems = new ObservableCollection<FileItemViewModel>(items);
            FilesListBox.ItemsSource = _fileItems;

            _fileCheckedSnapshot = files.ToDictionary(f => f, f => true);

            UpdateFilesProgress();

            if (_fileItems.Count > 0)
            {
                FilesListBox.SelectedIndex = 0;
            }
        }

        private void UpdateFilesProgress()
        {
            try
            {
                var stats = _dbService.GetFilesTranslationStats();
                foreach (var fileItem in _fileItems)
                {
                    if (stats.TryGetValue(fileItem.Name, out var fileStat))
                    {
                        int total = fileStat.Total;
                        int translated = fileStat.Translated;
                        double percent = total > 0 ? (double)translated / total * 100.0 : 0.0;
                        fileItem.ProgressPercent = percent;
                        fileItem.ProgressText = $"{translated}/{total} ({percent:F0}%)";
                    }
                    else
                    {
                        fileItem.ProgressPercent = 0.0;
                        fileItem.ProgressText = "0/0 (0%)";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private void VerifyRustFFI()
        {
            try
            {
                string version = NativeParser.GetVersion();
                AddTelemetryLog($"[rust-ffi] verified link. Core version: {version}");
            }
            catch (Exception ex)
            {
                AddTelemetryLog($"[rust-ffi] link failed: {ex.Message}");
            }
        }

        private void AddTelemetryLog(string text)
        {
            System.Diagnostics.Debug.WriteLine(text);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            // Ctrl + L: Toggle AI Assistant
            if (e.Key == Key.L && e.KeyModifiers == KeyModifiers.Control)
            {
                ToggleAiPanel();
                e.Handled = true;
            }
            // Ctrl + S: Save file shortcut
            else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
            {
                SaveActiveFile();
                e.Handled = true;
            }
            // Ctrl + N: New Project
            else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = CreateNewProjectAsync();
                e.Handled = true;
            }
            // Ctrl + O: Open Project
            else if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = OpenExistingProjectAsync();
                e.Handled = true;
            }
            // Ctrl + I: Import Folder
            else if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = OpenGameFolderAsync();
                e.Handled = true;
            }
        }

        private void ToggleAiPanel()
        {
            _isAiOpen = !_isAiOpen;
            if (_isAiOpen)
            {
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(300);
                AiSplitter.IsVisible = true;
                AiChatPanel.IsVisible = true;
                ToolTip.SetTip(AiToggleButton, "Hide AI Chat (Ctrl+L)");
            }
            else
            {
                WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
                AiSplitter.IsVisible = false;
                AiChatPanel.IsVisible = false;
                ToolTip.SetTip(AiToggleButton, "Show AI Chat (Ctrl+L)");
            }
        }

        private void SaveActiveFile()
        {
            var selectedFile = (FilesListBox.SelectedItem as FileItemViewModel)?.Name;
            if (string.IsNullOrEmpty(selectedFile)) return;

            foreach (var item in _translationItems)
            {
                _dbService.UpdateTranslation(item.Id, item.Translation);
                
                foreach (var meta in item.CustomColumns)
                {
                    _dbService.SaveCustomMeta(item.Id, meta.Key, meta.Value);
                }
            }

            SetStatus($"Saved changes for {selectedFile} containing {_translationItems.Count} strings to SQLite database.");
            UpdateFilesProgress();
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            await OpenGameFolderAsync();
        }

        private async Task OpenGameFolderAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Ren'Py Game Folder (game/ directory)",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;

            string folderPath = folders[0].Path.LocalPath;
            _lastGameFolder = folderPath;

            // Save game folder path to current project settings
            _dbService.SaveSetting("game_folder_path", folderPath);

            await ImportGameFolderFilesAsync(folderPath);
        }

        private async Task ImportGameFolderFilesAsync(string folderPath)
        {
            // Search for and handle .rpyc files first
            string[] rpycFiles = Directory.GetFiles(folderPath, "*.rpyc", SearchOption.AllDirectories);
            if (rpycFiles.Length > 0)
            {
                SetStatus($"Found {rpycFiles.Length} compiled .rpyc file(s). Running decompiler...");
                bool decompileSuccess = await _decompilerService.DecompileFolderIfNeededAsync(folderPath, msg => SetStatus(msg));
                if (!decompileSuccess)
                {
                    SetStatus("Failed to decompile all .rpyc files. Some dialogue may be missing. Proceeding to parse available .rpy files...");
                }
            }

            string[] rpyFiles = Directory.GetFiles(folderPath, "*.rpy", SearchOption.AllDirectories);

            if (rpyFiles.Length == 0)
            {
                SetStatus("No .rpy source files found in selected folder.");
                return;
            }

            int totalImported = 0;

            try
            {
                _fileItems.Clear();

                foreach (string filePath in rpyFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    var parsed = NativeParser.ParseRpyFile(filePath);

                    _dbService.ImportRpyFile(fileName, parsed);

                    if (!_fileItems.Any(f => f.Name == fileName))
                    {
                        _fileItems.Add(new FileItemViewModel { Name = fileName, IsChecked = true });
                    }

                    totalImported += parsed.Count;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Error during parsing and database import: {ex.Message}");
                return;
            }

            _fileCheckedSnapshot = _fileItems.ToDictionary(f => f.Name, f => f.IsChecked);

            UpdateFilesProgress();

            if (_fileItems.Count > 0)
            {
                FilesListBox.SelectedItem = _fileItems[0];
            }

            if (totalImported > 0)
            {
                SetStatus($"Imported {totalImported} dialogue strings from {rpyFiles.Length} file(s). Select a file in the sidebar to start translating.");
            }
            else if (rpyFiles.Length > 0)
            {
                SetStatus($"Imported {rpyFiles.Length} files. Selected files may contain only code.");
            }
        }

        private async void NewProject_Click(object? sender, RoutedEventArgs e)
        {
            await CreateNewProjectAsync();
        }

        private async Task CreateNewProjectAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Create New LocKit Project",
                DefaultExtension = "lockit",
                SuggestedFileName = "project.lockit",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("LocKit Projects")
                    {
                        Patterns = new[] { "*.lockit", "*.lkproj" }
                    }
                }
            });

            if (file == null) return;
            string projectPath = file.Path.LocalPath;

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Ren'Py Game Folder (game/ directory)",
                AllowMultiple = false
            });

            if (folders.Count == 0) return;
            string gameFolder = folders[0].Path.LocalPath;

            var prompt = new PromptWindow("New Project Target Language", "Enter target language (e.g. russian):", "russian");
            await prompt.ShowDialog(this);
            string targetLang = prompt.Result.Trim();
            if (string.IsNullOrEmpty(targetLang))
            {
                targetLang = "russian";
            }

            await CreateProjectAsync(projectPath, gameFolder, targetLang);
        }

        private async void OpenProject_Click(object? sender, RoutedEventArgs e)
        {
            await OpenExistingProjectAsync();
        }

        private async Task OpenExistingProjectAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open LocKit Project",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("LocKit Projects")
                    {
                        Patterns = new[] { "*.lockit", "*.lkproj" }
                    }
                }
            });

            if (files.Count == 0) return;
            string projectPath = files[0].Path.LocalPath;

            await LoadProjectAsync(projectPath);
        }

        private async Task CreateProjectAsync(string projectPath, string gameFolder, string targetLang)
        {
            try
            {
                SaveActiveFile();

                if (File.Exists(projectPath))
                {
                    File.Delete(projectPath);
                }

                _dbService.SetDatabasePath(projectPath);
                _dbService.InitializeDatabase(seedDemo: false);

                _dbService.SaveSetting("game_folder_path", gameFolder);
                _dbService.SaveSetting("default_target_language", targetLang);

                _dbService.SaveSetting("last_project_path", projectPath, isGlobal: true);

                _lastGameFolder = gameFolder;
                Title = $"LocKit - {Path.GetFileName(projectPath)}";

                SetStatus($"Project created. Importing game files from {gameFolder}...");
                await ImportGameFolderFilesAsync(gameFolder);

                SetStatus($"Project created and game files imported successfully!");
            }
            catch (Exception ex)
            {
                SetStatus($"Error creating project: {ex.Message}");
            }
        }

        private async Task LoadProjectAsync(string projectPath)
        {
            if (string.IsNullOrEmpty(projectPath) || !File.Exists(projectPath)) return;

            try
            {
                SaveActiveFile();

                _dbService.SetDatabasePath(projectPath);
                _dbService.InitializeDatabase(seedDemo: false);

                _lastGameFolder = _dbService.GetSetting("game_folder_path", "");
                string targetLang = _dbService.GetSetting("default_target_language", "russian");

                _dbService.SaveSetting("last_project_path", projectPath, isGlobal: true);

                SetupDataFromDb();

                Title = $"LocKit - {Path.GetFileName(projectPath)}";

                SetStatus($"Loaded project: {Path.GetFileName(projectPath)}. Target language: {targetLang}.");
            }
            catch (Exception ex)
            {
                SetStatus($"Error loading project: {ex.Message}");
            }
        }

        private void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            SaveActiveFile();
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            await ExportTlFolderAsync();
        }

        private Task ExportTlFolderAsync()
        {
            if (_fileItems.Count == 0 || string.IsNullOrEmpty(_lastGameFolder))
            {
                SetStatus("No game folder loaded. Open a game folder first.");
                return Task.CompletedTask;
            }

            // Save current file before exporting
            SaveActiveFile();

            string targetLanguage = _dbService.GetSetting("default_target_language", "russian");
            string tlRoot = Path.Combine(_lastGameFolder, "tl", targetLanguage);
            int exportedFiles = 0;
            int exportedStrings = 0;

            foreach (var item in _fileItems)
            {
                if (!item.IsChecked) continue;

                string fileName = item.Name;
                var units = _dbService.GetUnitsForExport(fileName);
                if (units.Count == 0) continue;

                string outputFilePath = Path.Combine(tlRoot, fileName);
                string? error = NativeParser.ExportTlFile(outputFilePath, units, targetLanguage);

                if (error == null)
                {
                    exportedFiles++;
                    exportedStrings += units.Count;
                }
                else
                {
                    SetStatus($"Export error for {fileName}: {error}");
                }
            }

            if (exportedFiles > 0)
            {
                SetStatus($"Export complete! {exportedStrings} strings across {exportedFiles} file(s) written directly to {tlRoot}");
            }
            else
            {
                SetStatus("No files were exported. Make sure files are checked in the sidebar.");
            }

            return Task.CompletedTask;
        }

        private void AddColumn_Click(object sender, RoutedEventArgs e)
        {
            string columnKey = $"notes_{_customColumnCounter}";
            string colHeader = $"Notes {_customColumnCounter}";
            _customColumnCounter++;

            // Create and bind new column (non-readonly so user can edit it in grid)
            var dataGridCol = new DataGridTextColumn
            {
                Header = colHeader,
                Binding = new Binding($"CustomColumns[{columnKey}]"),
                Width = new DataGridLength(150),
                IsReadOnly = false
            };
            TranslationGrid.Columns.Add(dataGridCol);

            // Populate empty/default metadata in memory for this column
            foreach (var item in _translationItems)
            {
                item.CustomColumns[columnKey] = $"Context {item.Id} info";
            }

            // Force DataGrid refresh
            TranslationGrid.ItemsSource = null;
            TranslationGrid.ItemsSource = _translationItems;

            SetStatus($"Added new custom column '{colHeader}'. Click Save (Ctrl+S) to persist.");
        }

        private void ToggleAiPanel_Click(object sender, RoutedEventArgs e)
        {
            ToggleAiPanel();
        }

        private void CheckVersion_Click(object sender, RoutedEventArgs e)
        {
            VerifyRustFFI();
        }

        private void FilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FilesListBox.SelectedItem is FileItemViewModel fileItem)
            {
                string selectedFile = fileItem.Name;
                var units = _dbService.GetTranslationUnits(selectedFile);
                _translationItems = new ObservableCollection<TranslationRow>(units);

                RestoreDynamicColumns(selectedFile);

                ApplyTableFilter();

                if (BreadcrumbTextBlock != null)
                {
                    BreadcrumbTextBlock.Text = Path.Combine("game", selectedFile).Replace('\\', '/');
                }

                if (_translationItems.Count > 0)
                {
                    TranslationGrid.SelectedIndex = 0;
                }
                else
                {
                    TranslationGrid.SelectedIndex = -1;
                    _selectedRow = null;
                    OriginalContextTextBox.Text = string.Empty;
                    TranslationContextTextBox.Text = string.Empty;
                }

                SetStatus($"Loaded file: {selectedFile} ({_translationItems.Count} strings)");
            }
        }

        private void RestoreDynamicColumns(string fileName)
        {
            // Remove any dynamically added columns (keep the first 4 default columns)
            while (TranslationGrid.Columns.Count > 4)
            {
                TranslationGrid.Columns.RemoveAt(4);
            }

            // Load meta keys that exist in DB for this file
            var keys = _dbService.GetCustomMetaKeys(fileName);
            foreach (var key in keys)
            {
                string headerName = key;
                if (key.StartsWith("notes_") && int.TryParse(key.Substring(6), out int num))
                {
                    headerName = $"Notes {num}";
                    if (num >= _customColumnCounter)
                    {
                        _customColumnCounter = num + 1;
                    }
                }

                var dataGridCol = new DataGridTextColumn
                {
                    Header = headerName,
                    Binding = new Binding($"CustomColumns[{key}]"),
                    Width = new DataGridLength(150),
                    IsReadOnly = false
                };
                TranslationGrid.Columns.Add(dataGridCol);
            }
        }

        private void TranslationGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selectedRow != null)
            {
                _selectedRow.PropertyChanged -= SelectedRow_PropertyChanged;
            }

            if (TranslationGrid.SelectedItem is TranslationRow selectedRow)
            {
                _selectedRow = selectedRow;
                _selectedRow.PropertyChanged += SelectedRow_PropertyChanged;
                OriginalContextTextBox.Text = selectedRow.Original;
                TranslationContextTextBox.Text = selectedRow.Translation;

                if (PropertyRowIdText != null) PropertyRowIdText.Text = selectedRow.Id.ToString();
                if (PropertyKeyText != null) PropertyKeyText.Text = selectedRow.Key;
                if (PropertyFileText != null)
                {
                    var selectedFile = (FilesListBox.SelectedItem as FileItemViewModel)?.Name ?? "unknown";
                    PropertyFileText.Text = selectedFile;
                }
            }
            else
            {
                _selectedRow = null;
                OriginalContextTextBox.Text = string.Empty;
                TranslationContextTextBox.Text = string.Empty;

                if (PropertyRowIdText != null) PropertyRowIdText.Text = "N/A";
                if (PropertyKeyText != null) PropertyKeyText.Text = "N/A";
                if (PropertyFileText != null) PropertyFileText.Text = "N/A";
            }
        }

        private void SelectedRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TranslationRow.Translation) && _selectedRow != null)
            {
                if (TranslationContextTextBox.Text != _selectedRow.Translation)
                {
                    TranslationContextTextBox.Text = _selectedRow.Translation;
                }
                UpdateStats();
            }
        }

        private void TranslationContextTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_selectedRow != null && TranslationGrid.SelectedItem is TranslationRow gridRow)
            {
                if (gridRow.Translation != TranslationContextTextBox.Text)
                {
                    gridRow.Translation = TranslationContextTextBox.Text ?? string.Empty;
                }
                UpdateStats();
            }
        }

        private void SendAiMessage_Click(object sender, RoutedEventArgs e)
        {
            ProcessAiInput();
        }

        private void AiInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ProcessAiInput();
                e.Handled = true;
            }
        }

        private async void ProcessAiInput()
        {
            string prompt = AiInputTextBox.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt)) return;

            AiInputTextBox.Text = string.Empty;
            AddUserChatBubble(prompt);

            string baseUrl = _dbService.GetSetting("llm_base_url", "https://api.openai.com/v1", isGlobal: true);
            string apiKey = _dbService.GetSetting("llm_api_key", "", isGlobal: true);
            string model = _dbService.GetSetting("llm_model", "gpt-4o", isGlobal: true);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                AddAiChatBubble("Error: LLM API Key is not configured. Please fill in the LLM Settings in the AI panel above to start using the AI Assistant.");
                return;
            }

            string systemPrompt = "You are an expert game localization assistant for Ren'Py. Help the user refine their translations, verify context, and ensure consistency.";
            if (_selectedRow != null)
            {
                string activeFile = (FilesListBox.SelectedItem as FileItemViewModel)?.Name ?? "unknown";
                systemPrompt += $"\n\nContext of the current string being translated:\n- File: {activeFile}\n- Key/ID: {_selectedRow.Key}\n- Original Text: \"{_selectedRow.Original}\"\n- Current Translation: \"{_selectedRow.Translation}\"\n\nPlease provide help, suggestions, or answer questions considering this translation context.";
            }

            var thinkingBubble = AddAiChatBubble("Thinking...");
            string response = await _llmService.GetAiResponseAsync(baseUrl, apiKey, model, systemPrompt, prompt);
            
            ChatHistoryPanel.Children.Remove(thinkingBubble);
            AddAiChatBubble(response);
        }

        private void LoadLlmSettings()
        {
            LlmBaseUrlTextBox.Text = _dbService.GetSetting("llm_base_url", "https://api.openai.com/v1", isGlobal: true);
            LlmApiKeyTextBox.Text = _dbService.GetSetting("llm_api_key", "", isGlobal: true);
            LlmModelTextBox.Text = _dbService.GetSetting("llm_model", "gpt-4o", isGlobal: true);
        }

        private void SaveLlmSettings_Click(object sender, RoutedEventArgs e)
        {
            _dbService.SaveSetting("llm_base_url", LlmBaseUrlTextBox.Text ?? string.Empty, isGlobal: true);
            _dbService.SaveSetting("llm_api_key", LlmApiKeyTextBox.Text ?? string.Empty, isGlobal: true);
            _dbService.SaveSetting("llm_model", LlmModelTextBox.Text ?? string.Empty, isGlobal: true);
            SetStatus("LLM settings saved successfully!");
        }

        private void AddUserChatBubble(string message)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#161622")),
                BorderBrush = new SolidColorBrush(Color.Parse("#252535")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Thickness(24, 4, 0, 0)
            };
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "You", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#888888")), FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = message, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")), TextWrapping = TextWrapping.Wrap });
            border.Child = panel;
            ChatHistoryPanel.Children.Add(border);
        }

        private Border AddAiChatBubble(string message)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#100F17")),
                BorderBrush = new SolidColorBrush(Color.Parse("#25193B")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 24, 0)
            };
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "LocKit AI", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#A78BFA")), FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = message, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")), TextWrapping = TextWrapping.Wrap });
            border.Child = panel;
            ChatHistoryPanel.Children.Add(border);
            return border;
        }

        private void InitializeGridContextMenu()
        {
            var menu = new ContextMenu();
            
            menu.Opened += (s, e) =>
            {
                menu.ItemsSource = null;
                var items = new List<MenuItem>();
                
                // 1. If a row is selected, show "Translate with AI" and "Translate with Google (Free)"
                if (_selectedRow != null)
                {
                    string targetLang = _dbService.GetSetting("default_target_language", "russian");
                    string targetShort = targetLang.Length >= 2 ? targetLang.Substring(0, 2).ToLower() : "ru";
                    
                    var translateItem = new MenuItem
                    {
                        Header = $"Translate with AI (en -> {targetShort})",
                        Foreground = SolidColorBrush.Parse("#D8B4FE")
                    };
                    translateItem.Click += async (sender, args) =>
                    {
                        await TranslateActiveRowWithAiAsync();
                    };
                    items.Add(translateItem);

                    var translateGoogleItem = new MenuItem
                    {
                        Header = $"Translate with Google (Free)",
                        Foreground = SolidColorBrush.Parse("#93C5FD")
                    };
                    translateGoogleItem.Click += async (sender, args) =>
                    {
                        await TranslateActiveRowWithGoogleFreeAsync();
                    };
                    items.Add(translateGoogleItem);

                    var wrapCurrentItem = new MenuItem
                    {
                        Header = "Auto-wrap current translation (45 chars)"
                    };
                    wrapCurrentItem.Click += (sender, args) =>
                    {
                        if (_selectedRow != null && !string.IsNullOrEmpty(_selectedRow.Translation))
                        {
                            string wrapped = TextProcessor.WordWrap(_selectedRow.Translation, 45);
                            _selectedRow.Translation = wrapped;
                            _dbService.UpdateTranslation(_selectedRow.Id, wrapped);
                            if (TranslationGrid.SelectedItem == _selectedRow)
                            {
                                TranslationContextTextBox.Text = wrapped;
                            }
                            SetStatus("Applied Word Wrap to selected row.");
                        }
                    };
                    items.Add(wrapCurrentItem);

                    var wrapAllItem = new MenuItem
                    {
                        Header = "Auto-wrap all translations in file (45 chars)"
                    };
                    wrapAllItem.Click += (sender, args) =>
                    {
                        if (_translationItems != null && _translationItems.Count > 0)
                        {
                            int count = 0;
                            foreach (var row in _translationItems)
                            {
                                if (!string.IsNullOrEmpty(row.Translation))
                                {
                                    string wrapped = TextProcessor.WordWrap(row.Translation, 45);
                                    if (row.Translation != wrapped)
                                    {
                                        row.Translation = wrapped;
                                        _dbService.UpdateTranslation(row.Id, wrapped);
                                        count++;
                                    }
                                }
                            }
                            if (_selectedRow != null && TranslationGrid.SelectedItem == _selectedRow)
                            {
                                TranslationContextTextBox.Text = _selectedRow.Translation;
                            }
                            SetStatus($"Auto-wrapped {count} translations in the active file.");
                        }
                    };
                    items.Add(wrapAllItem);

                    items.Add(new MenuItem { Header = "-" });
                }

                // 2. Add checkbox for each column visibility
                foreach (var col in TranslationGrid.Columns)
                {
                    var header = col.Header?.ToString() ?? "Column";
                    var checkBox = new CheckBox
                    {
                        IsChecked = col.IsVisible,
                        IsHitTestVisible = false,
                        Margin = new Thickness(0, 0, 8, 0)
                    };
                    
                    var item = new MenuItem
                    {
                        Header = header,
                        Icon = checkBox
                    };
                    item.Click += (sender, args) =>
                    {
                        col.IsVisible = !col.IsVisible;
                    };
                    items.Add(item);
                }
                
                items.Add(new MenuItem { Header = "-" });
                
                // 3. Option to Add Custom Column
                var addColItem = new MenuItem { Header = "Add Custom Column..." };
                addColItem.Click += async (sender, args) =>
                {
                    var prompt = new PromptWindow("Add Custom Column", "Enter column name:");
                    await prompt.ShowDialog(this);
                    if (!string.IsNullOrWhiteSpace(prompt.Result))
                    {
                        AddCustomColumn(prompt.Result);
                    }
                };
                items.Add(addColItem);
                
                menu.ItemsSource = items;
            };
            
            TranslationGrid.ContextMenu = menu;
        }

        private async Task TranslateActiveRowWithAiAsync()
        {
            if (_selectedRow == null) return;

            string baseUrl = _dbService.GetSetting("llm_base_url", "https://api.openai.com/v1", isGlobal: true);
            string apiKey = _dbService.GetSetting("llm_api_key", "", isGlobal: true);
            string model = _dbService.GetSetting("llm_model", "gpt-4o", isGlobal: true);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetStatus("Error: LLM API Key is not configured. Please fill in the LLM Settings in the AI panel to use AI translation.");
                return;
            }

            string targetLang = _dbService.GetSetting("default_target_language", "russian");

            string systemPrompt = $"You are a professional game translator. Translate the text from English to {targetLang}. " +
                                  "Return ONLY the translated text, with no explanations, no notes, and no quotes unless they are part of the translation.";
            
            string originalText = _selectedRow.Original;

            SetStatus($"AI translating: \"{originalText}\"...");

            string translation = await _llmService.GetAiResponseAsync(baseUrl, apiKey, model, systemPrompt, originalText);

            if (!translation.StartsWith("Error") && !translation.StartsWith("Exception"))
            {
                translation = translation.Trim('\"', '\'');
                
                _selectedRow.Translation = translation;
                _dbService.UpdateTranslation(_selectedRow.Id, translation);
                
                if (TranslationGrid.SelectedItem == _selectedRow)
                {
                    TranslationContextTextBox.Text = translation;
                }

                SetStatus($"AI translation successful: \"{translation}\"");
                UpdateFilesProgress();
            }
            else
            {
                SetStatus($"AI translation failed: {translation}");
            }
        }

        private async void CreateNewTable_Click(object? sender, RoutedEventArgs e)
        {
            var prompt = new PromptWindow("Create New Table", "Enter new table/file name (e.g. script_custom.rpy):");
            await prompt.ShowDialog(this);
            
            string fileName = prompt.Result.Trim();
            if (string.IsNullOrWhiteSpace(fileName)) return;

            if (!fileName.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".rpy";
            }

            if (_fileItems.Any(f => f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"Error: A table named '{fileName}' already exists.");
                return;
            }

            _dbService.ImportRpyFile(fileName, new List<RpyDialogueLine>());
            
            var newItem = new FileItemViewModel { Name = fileName, IsChecked = true };
            _fileItems.Add(newItem);
            _fileCheckedSnapshot[fileName] = true;
            FilesListBox.SelectedItem = newItem;

            SetStatus($"Created new empty translation table: {fileName}");
        }

        private void AddCustomColumn(string headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName)) return;
            string columnKey = headerName.Replace(" ", "_").ToLower();

            foreach (var col in TranslationGrid.Columns)
            {
                if (string.Equals(col.Header?.ToString(), headerName, StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus($"Error: Column '{headerName}' already exists.");
                    return;
                }
            }

            var dataGridCol = new DataGridTextColumn
            {
                Header = headerName,
                Binding = new Binding($"CustomColumns[{columnKey}]"),
                Width = new DataGridLength(150),
                IsReadOnly = false
            };
            TranslationGrid.Columns.Add(dataGridCol);

            foreach (var item in _translationItems)
            {
                if (!item.CustomColumns.ContainsKey(columnKey))
                {
                    item.CustomColumns[columnKey] = string.Empty;
                }
            }

            TranslationGrid.ItemsSource = null;
            TranslationGrid.ItemsSource = _translationItems;

            SetStatus($"Added new custom column '{headerName}'. You can now edit notes in each row. Click Save (Ctrl+S) to persist it.");
        }

        private void SetStatus(string message)
        {
            StatusTextBlock.Text = message;
            System.Diagnostics.Debug.WriteLine(message);
        }

        private void SavePreBulkSnapshot()
        {
            _fileCheckedSnapshot = _fileItems.ToDictionary(f => f.Name, f => f.IsChecked);
        }

        private void SelectAllFiles_Click(object? sender, RoutedEventArgs e)
        {
            SavePreBulkSnapshot();
            foreach (var item in _fileItems)
            {
                item.IsChecked = true;
            }
            SetStatus("Selected all files.");
        }

        private void SelectNoFiles_Click(object? sender, RoutedEventArgs e)
        {
            SavePreBulkSnapshot();
            foreach (var item in _fileItems)
            {
                item.IsChecked = false;
            }
            SetStatus("Deselected all files.");
        }

        private void InvertFiles_Click(object? sender, RoutedEventArgs e)
        {
            SavePreBulkSnapshot();
            foreach (var item in _fileItems)
            {
                item.IsChecked = !item.IsChecked;
            }
            SetStatus("Inverted file selection.");
        }

        private void RestoreFiles_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var item in _fileItems)
            {
                if (_fileCheckedSnapshot.TryGetValue(item.Name, out bool val))
                {
                    item.IsChecked = val;
                }
            }
            SetStatus("Restored file selection states.");
        }

        private void SearchTextBox_TextChanged(object? sender, TextChangedEventArgs e)
        {
            ApplyTableFilter();
        }

        private void ShowOnlyUntranslatedCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            ApplyTableFilter();
        }

        private void ApplyTableFilter()
        {
            if (_translationItems == null) return;

            string search = SearchTextBox?.Text ?? string.Empty;
            bool onlyUntranslated = ShowOnlyUntranslatedCheckBox?.IsChecked == true;

            var filtered = _translationItems.Where(row =>
            {
                if (onlyUntranslated && !string.IsNullOrEmpty(row.Translation))
                    return false;

                if (!string.IsNullOrEmpty(search))
                {
                    bool matchOriginal = row.Original?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
                    bool matchTranslation = row.Translation?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
                    bool matchKey = row.Key?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;
                    return matchOriginal || matchTranslation || matchKey;
                }

                return true;
            }).ToList();

            TranslationGrid.ItemsSource = filtered;
            UpdateStats();
        }

        private void UpdateStats()
        {
            if (_translationItems == null || GridProgressTextBlock == null) return;
            int total = _translationItems.Count;
            int translated = _translationItems.Count(row => !string.IsNullOrEmpty(row.Translation));
            GridProgressTextBlock.Text = $"{translated} / {total}";
        }

        private async void BatchTranslateGoogleFree_Click(object? sender, RoutedEventArgs e)
        {
            if (_translationItems == null || _translationItems.Count == 0)
            {
                SetStatus("No strings to translate in this file.");
                return;
            }

            var untranslated = _translationItems.Where(row => string.IsNullOrEmpty(row.Translation)).ToList();
            if (untranslated.Count == 0)
            {
                SetStatus("All strings in this file are already translated!");
                return;
            }

            SetStatus($"Batch translating {untranslated.Count} strings via Google Translate (Free)...");

            int translatedCount = 0;
            string targetLanguage = _dbService.GetSetting("default_target_language", "russian");
            string targetShort = targetLanguage.Length >= 2 ? targetLanguage.Substring(0, 2).ToLower() : "ru";

            foreach (var row in untranslated)
            {
                string translation = await _llmService.TranslateWithGoogleFreeAsync(row.Original, targetShort);

                if (!translation.StartsWith("Error") && !translation.StartsWith("Exception"))
                {
                    row.Translation = translation;
                    _dbService.UpdateTranslation(row.Id, translation);
                    translatedCount++;
                    SetStatus($"Translated {translatedCount} / {untranslated.Count} strings...");
                }
                else
                {
                    SetStatus($"Translation stopped due to error: {translation}");
                    break;
                }

                await Task.Delay(100);
            }

            SetStatus($"Batch translation complete! Translated {translatedCount} strings.");
            ApplyTableFilter();
            UpdateFilesProgress();
        }

        private async Task TranslateActiveRowWithGoogleFreeAsync()
        {
            if (_selectedRow == null) return;

            string targetLang = _dbService.GetSetting("default_target_language", "russian");
            string targetShort = targetLang.Length >= 2 ? targetLang.Substring(0, 2).ToLower() : "ru";
            string originalText = _selectedRow.Original;

            SetStatus($"Translating via Google: \"{originalText}\"...");

            string translation = await _llmService.TranslateWithGoogleFreeAsync(originalText, targetShort);

            if (!translation.StartsWith("Error") && !translation.StartsWith("Exception"))
            {
                _selectedRow.Translation = translation;
                _dbService.UpdateTranslation(_selectedRow.Id, translation);
                
                if (TranslationGrid.SelectedItem == _selectedRow)
                {
                    TranslationContextTextBox.Text = translation;
                }

                SetStatus($"Google translation successful: \"{translation}\"");
                ApplyTableFilter();
                UpdateFilesProgress();
            }
            else
            {
                SetStatus($"Google translation failed: {translation}");
            }
        }
    }

    public class FileItemViewModel : INotifyPropertyChanged
    {
        private bool _isChecked = true;
        private string _name = string.Empty;
        private string _progressText = string.Empty;
        private double _progressPercent = 0.0;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public bool IsChecked
        {
            get => _isChecked;
            set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(nameof(ProgressText)); }
        }

        public double ProgressPercent
        {
            get => _progressPercent;
            set { _progressPercent = value; OnPropertyChanged(nameof(ProgressPercent)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}