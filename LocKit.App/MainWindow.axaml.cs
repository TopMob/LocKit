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
        private ObservableCollection<string> _fileItems = new();
        private bool _isAiOpen = true;
        private int _customColumnCounter = 1;
        private TranslationRow? _selectedRow;
        private string _lastGameFolder = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
            
            // Initialize database schema and seed demo data
            _dbService.InitializeDatabase();

            SetupDataFromDb();
            
            // Auto-verify FFI connection at startup
            VerifyRustFFI();

            LoadLlmSettings();
        }

        private void SetupDataFromDb()
        {
            // Load files list from SQLite
            var files = _dbService.GetFiles();
            _fileItems = new ObservableCollection<string>(files);
            FilesListBox.ItemsSource = _fileItems;

            if (_fileItems.Count > 0)
            {
                FilesListBox.SelectedIndex = 0;
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
        }

        private void ToggleAiPanel()
        {
            _isAiOpen = !_isAiOpen;
            if (_isAiOpen)
            {
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(300);
                AiSplitter.IsVisible = true;
                AiChatPanel.IsVisible = true;
                AiToggleButton.Content = "Hide AI Chat";
            }
            else
            {
                WorkspaceGrid.ColumnDefinitions[4].Width = new GridLength(0);
                AiSplitter.IsVisible = false;
                AiChatPanel.IsVisible = false;
                AiToggleButton.Content = "AI Chat (Ctrl+L)";
            }
        }

        private void SaveActiveFile()
        {
            var selectedFile = FilesListBox.SelectedItem as string;
            if (string.IsNullOrEmpty(selectedFile)) return;

            // Save all translation items of the active file to SQLite database
            foreach (var item in _translationItems)
            {
                _dbService.UpdateTranslation(item.Id, item.Translation);
                
                // Save custom columns/meta
                foreach (var meta in item.CustomColumns)
                {
                    _dbService.SaveCustomMeta(item.Id, meta.Key, meta.Value);
                }
            }

            var border = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1E1B4E")),
                BorderBrush = new SolidColorBrush(Color.Parse("#4C1D95")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 4, 0, 0)
            };
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "System Log", FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#F472B6")), FontWeight = FontWeight.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"Saved changes for {selectedFile} containing {_translationItems.Count} strings to SQLite database.", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#A0A0AA")), TextWrapping = TextWrapping.Wrap });
            border.Child = panel;
            ChatHistoryPanel.Children.Add(border);
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
            
            // Search for and handle .rpyc files first
            string[] rpycFiles = Directory.GetFiles(folderPath, "*.rpyc", SearchOption.AllDirectories);
            if (rpycFiles.Length > 0)
            {
                AddAiChatBubble($"Found {rpycFiles.Length} compiled .rpyc file(s). Running decompiler...");
                bool decompileSuccess = await _decompilerService.DecompileFolderIfNeededAsync(folderPath, msg => AddAiChatBubble(msg));
                if (!decompileSuccess)
                {
                    AddAiChatBubble("Failed to decompile all .rpyc files. Some dialogue may be missing. Proceeding to parse available .rpy files...");
                }
            }

            string[] rpyFiles = Directory.GetFiles(folderPath, "*.rpy", SearchOption.AllDirectories);

            if (rpyFiles.Length == 0)
            {
                AddAiChatBubble($"No .rpy source files found in selected folder.");
                return;
            }

            int totalImported = 0;

            // Parse all .rpy files
            foreach (string filePath in rpyFiles)
            {
                string fileName = Path.GetFileName(filePath);
                var parsed = NativeParser.ParseRpyFile(filePath);
                if (parsed.Count == 0) continue;

                _dbService.ImportRpyFile(fileName, parsed);

                if (!_fileItems.Contains(fileName))
                    _fileItems.Add(fileName);

                totalImported += parsed.Count;
            }

            // Handle .rpyc files
            if (rpycFiles.Length > 0)
            {
                string rpycList = string.Join(", ", rpycFiles.Select(Path.GetFileName));
                AddAiChatBubble($"Found {rpycFiles.Length} compiled .rpyc file(s): {rpycList}\nDecompilation support coming soon. For now, if the game folder contains .rpy source files too, they are already imported.");
            }

            if (totalImported > 0)
            {
                // Auto-select first file
                if (_fileItems.Count > 0)
                    FilesListBox.SelectedItem = _fileItems[0];

                AddAiChatBubble($"Imported {totalImported} dialogue strings from {rpyFiles.Length} file(s). Select a file in the sidebar to start translating.");
            }
            else if (rpyFiles.Length > 0)
            {
                AddAiChatBubble($"Found {rpyFiles.Length} .rpy file(s) but no dialogue strings were extracted. Files may contain only code.");
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

        private async Task ExportTlFolderAsync()
        {
            if (_fileItems.Count == 0)
            {
                AddAiChatBubble("No files loaded. Open a game folder first.");
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            // Ask user where to export (default: alongside game folder)
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select output folder (tl/russian/ will be created inside)",
                AllowMultiple = false
            });

            string outputBase = folders.Count > 0
                ? folders[0].Path.LocalPath
                : _lastGameFolder;

            if (string.IsNullOrEmpty(outputBase))
            {
                AddAiChatBubble("Export cancelled: no output folder selected.");
                return;
            }

            // Save current file before exporting
            SaveActiveFile();

            string tlRoot = Path.Combine(outputBase, "tl", "russian");
            int exportedFiles = 0;
            int exportedStrings = 0;

            foreach (string fileName in _fileItems)
            {
                var units = _dbService.GetUnitsForExport(fileName);
                if (units.Count == 0) continue;

                string outputFilePath = Path.Combine(tlRoot, fileName);
                string? error = NativeParser.ExportTlFile(outputFilePath, units, "russian");

                if (error == null)
                {
                    exportedFiles++;
                    exportedStrings += units.Count;
                }
                else
                {
                    AddAiChatBubble($"Export error for {fileName}: {error}");
                }
            }

            if (exportedFiles > 0)
            {
                AddAiChatBubble(
                    $"Export complete. {exportedStrings} strings across {exportedFiles} file(s) written to:\n{tlRoot}\n\n" +
                    $"To enable in Ren'Py, add to options.rpy:\n    define config.language = \"russian\"");
            }
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

            AddAiChatBubble($"Added new custom column '{colHeader}'. You can now associate notes or custom meta with each translation row. Click Save (Ctrl+S) to persist it.");
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
            if (FilesListBox.SelectedItem is string selectedFile)
            {
                // Load units from SQLite database for the selected file
                var units = _dbService.GetTranslationUnits(selectedFile);
                _translationItems = new ObservableCollection<TranslationRow>(units);

                // Restore columns from DB
                RestoreDynamicColumns(selectedFile);

                TranslationGrid.ItemsSource = _translationItems;
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

                AddAiChatBubble($"Loaded translation schema for '{selectedFile}'. Ready to query.");
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
            // Unsubscribe from previous selected row's PropertyChanged event
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
            }
            else
            {
                _selectedRow = null;
                OriginalContextTextBox.Text = string.Empty;
                TranslationContextTextBox.Text = string.Empty;
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

            string baseUrl = _dbService.GetSetting("llm_base_url", "https://api.openai.com/v1");
            string apiKey = _dbService.GetSetting("llm_api_key", "");
            string model = _dbService.GetSetting("llm_model", "gpt-4o");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                AddAiChatBubble("Error: LLM API Key is not configured. Please fill in the LLM Settings in the AI panel above to start using the AI Assistant.");
                return;
            }

            string systemPrompt = "You are an expert game localization assistant for Ren'Py. Help the user refine their translations, verify context, and ensure consistency.";
            if (_selectedRow != null)
            {
                string activeFile = FilesListBox.SelectedItem as string ?? "unknown";
                systemPrompt += $"\n\nContext of the current string being translated:\n- File: {activeFile}\n- Key/ID: {_selectedRow.Key}\n- Original Text: \"{_selectedRow.Original}\"\n- Current Translation: \"{_selectedRow.Translation}\"\n\nPlease provide help, suggestions, or answer questions considering this translation context.";
            }

            var thinkingBubble = AddAiChatBubble("Thinking...");
            string response = await _llmService.GetAiResponseAsync(baseUrl, apiKey, model, systemPrompt, prompt);
            
            ChatHistoryPanel.Children.Remove(thinkingBubble);
            AddAiChatBubble(response);
        }

        private void LoadLlmSettings()
        {
            LlmBaseUrlTextBox.Text = _dbService.GetSetting("llm_base_url", "https://api.openai.com/v1");
            LlmApiKeyTextBox.Text = _dbService.GetSetting("llm_api_key", "");
            LlmModelTextBox.Text = _dbService.GetSetting("llm_model", "gpt-4o");
        }

        private void SaveLlmSettings_Click(object sender, RoutedEventArgs e)
        {
            _dbService.SaveSetting("llm_base_url", LlmBaseUrlTextBox.Text ?? string.Empty);
            _dbService.SaveSetting("llm_api_key", LlmApiKeyTextBox.Text ?? string.Empty);
            _dbService.SaveSetting("llm_model", LlmModelTextBox.Text ?? string.Empty);
            AddAiChatBubble("LLM settings saved successfully!");
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
    }
}