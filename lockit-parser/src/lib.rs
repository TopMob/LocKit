use std::ffi::{c_char, CString, CStr};
use std::io::Write;
use serde::{Serialize, Deserialize};

#[derive(Serialize)]
struct ParsedUnit {
    key: String,
    character: String,
    source: String,
}

#[unsafe(no_mangle)]
pub extern "C" fn get_parser_version() -> *mut c_char {
    let s = CString::new("LocKit Parser v0.2.0 (Ren'Py Ready)").unwrap();
    s.into_raw()
}

#[unsafe(no_mangle)]
pub extern "C" fn free_string(s: *mut c_char) {
    if s.is_null() {
        return;
    }
    unsafe {
        let _ = CString::from_raw(s);
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn parse_rpy_file(path_ptr: *const c_char) -> *mut c_char {
    if path_ptr.is_null() {
        let err = serde_json::json!({"error": "null path"}).to_string();
        return CString::new(err).unwrap().into_raw();
    }

    let path = unsafe { CStr::from_ptr(path_ptr) }
        .to_string_lossy()
        .into_owned();

    let content = match std::fs::read_to_string(&path) {
        Ok(c) => c,
        Err(e) => {
            let err = serde_json::json!({"error": format!("read error: {}", e)}).to_string();
            return CString::new(err).unwrap().into_raw();
        }
    };

    let units = parse_rpy_content(&content);
    let json = serde_json::to_string(&units).unwrap_or_else(|e| {
        serde_json::json!({"error": format!("serialize error: {}", e)}).to_string()
    });

    CString::new(json).unwrap().into_raw()
}

fn parse_rpy_content(content: &str) -> Vec<ParsedUnit> {
    let mut units = Vec::new();
    let mut unit_index: usize = 0;

    let mut lines = content.lines().peekable();

    while let Some(line) = lines.next() {
        let trimmed = line.trim();

        // Skip comments and blank lines
        if trimmed.starts_with('#') || trimmed.is_empty() {
            continue;
        }

        // Handle translate blocks: translate <lang> <label>:
        if trimmed.starts_with("translate ") && trimmed.ends_with(':') {
            let label = extract_translate_label(trimmed);
            
            // Look ahead for old/new pairs inside the block
            let mut old_text = String::new();

            while let Some(inner_line) = lines.peek() {
                let inner = inner_line.trim();
                
                if inner.starts_with("old ") {
                    old_text = extract_quoted(inner.strip_prefix("old ").unwrap_or("")).to_string();
                    lines.next();
                } else if inner.starts_with("new ") {
                    // new "" means untranslated — exactly what we want to fill
                    lines.next();
                    if !old_text.is_empty() {
                        unit_index += 1;
                        units.push(ParsedUnit {
                            key: format!("tl_{}", label),
                            character: String::new(),
                            source: old_text.clone(),
                        });
                        old_text.clear();
                    }
                } else if inner.is_empty() || inner.starts_with('#') {
                    lines.next();
                } else {
                    break;
                }
            }
            continue;
        }

        // Handle character dialogue: Name "text" or name "text"
        if let Some((character, text)) = parse_dialogue_line(trimmed) {
            unit_index += 1;
            units.push(ParsedUnit {
                key: format!("line_{:04}", unit_index),
                character,
                source: text,
            });
            continue;
        }

        // Handle narrator lines: "text" (no character prefix)
        if trimmed.starts_with('"') && trimmed.ends_with('"') && trimmed.len() > 2 {
            let text = trimmed[1..trimmed.len() - 1].to_string();
            if !text.is_empty() {
                unit_index += 1;
                units.push(ParsedUnit {
                    key: format!("narr_{:04}", unit_index),
                    character: String::new(),
                    source: text,
                });
            }
        }
    }

    units
}

fn parse_dialogue_line(line: &str) -> Option<(String, String)> {
    // Pattern: WORD "text here" or WORD "text" with optional extras
    // Character names are typically identifiers (alphanumeric + underscore)
    // followed by a space and a quoted string.
    let parts: Vec<&str> = line.splitn(2, ' ').collect();
    if parts.len() < 2 {
        return None;
    }

    let character_candidate = parts[0];
    let rest = parts[1].trim();

    // Character must be a valid identifier (no quotes, no special chars)
    if character_candidate.contains('"') || character_candidate.contains('(') {
        return None;
    }
    // Reject Ren'Py keywords
    let keywords = ["if", "elif", "else", "while", "for", "with", "show", "hide",
                    "play", "stop", "pause", "scene", "label", "menu", "call",
                    "return", "jump", "define", "default", "init", "python",
                    "image", "transform", "style", "nvl", "$"];
    if keywords.contains(&character_candidate) {
        return None;
    }

    // Rest must start and end with a quote
    if rest.starts_with('"') {
        let text = extract_quoted(rest);
        if !text.is_empty() {
            return Some((character_candidate.to_string(), text.to_string()));
        }
    }

    None
}

fn extract_quoted(s: &str) -> &str {
    let s = s.trim();
    if s.starts_with('"') && s.len() > 1 {
        // Find the closing quote, handling escaped quotes
        let inner = &s[1..];
        let mut end = 0;
        let mut escaped = false;
        for (i, c) in inner.char_indices() {
            if escaped {
                escaped = false;
            } else if c == '\\' {
                escaped = true;
            } else if c == '"' {
                end = i;
                break;
            }
        }
        &inner[..end]
    } else {
        s
    }
}

fn extract_translate_label(line: &str) -> String {
    // translate russian my_label:  ->  my_label
    let parts: Vec<&str> = line.split_whitespace().collect();
    if parts.len() >= 3 {
        parts[2].trim_end_matches(':').to_string()
    } else {
        String::from("unknown")
    }
}

#[derive(Deserialize)]
struct TlUnit {
    key: String,
    source: String,
    target: String,
}

/// Generates a Ren'Py translation file at `output_path` from a JSON array of translation units.
/// Each unit must have: key, source, target.
/// Returns null on success or a JSON error string on failure.
#[unsafe(no_mangle)]
pub extern "C" fn export_tl_file(
    output_path_ptr: *const c_char,
    units_json_ptr: *const c_char,
    language_ptr: *const c_char,
) -> *mut c_char {
    let output_path = match ptr_to_string(output_path_ptr) {
        Some(s) => s,
        None => return error_cstring("null output_path"),
    };
    let units_json = match ptr_to_string(units_json_ptr) {
        Some(s) => s,
        None => return error_cstring("null units_json"),
    };
    let language = match ptr_to_string(language_ptr) {
        Some(s) => s,
        None => String::from("russian"),
    };

    let units: Vec<TlUnit> = match serde_json::from_str(&units_json) {
        Ok(u) => u,
        Err(e) => return error_cstring(&format!("json parse error: {}", e)),
    };

    if let Some(parent) = std::path::Path::new(&output_path).parent() {
        if let Err(e) = std::fs::create_dir_all(parent) {
            return error_cstring(&format!("mkdir error: {}", e));
        }
    }

    let mut file = match std::fs::File::create(&output_path) {
        Ok(f) => f,
        Err(e) => return error_cstring(&format!("file create error: {}", e)),
    };

    let source_comment = format!("# {}", output_path);
    let _ = writeln!(file, "{}", source_comment);
    let _ = writeln!(file);

    for unit in &units {
        let _ = writeln!(file, "translate {} {}:", language, unit.key);
        let _ = writeln!(file);
        let _ = writeln!(file, "    # \"{}\"", unit.source.replace('"', "\\\""));
        let _ = writeln!(file, "    old \"{}\"", unit.source.replace('"', "\\\""));
        let _ = writeln!(file, "    new \"{}\"", unit.target.replace('"', "\\\""));
        let _ = writeln!(file);
    }

    std::ptr::null_mut()
}

fn ptr_to_string(ptr: *const c_char) -> Option<String> {
    if ptr.is_null() {
        return None;
    }
    Some(unsafe { CStr::from_ptr(ptr) }.to_string_lossy().into_owned())
}

fn error_cstring(msg: &str) -> *mut c_char {
    let json = serde_json::json!({"error": msg}).to_string();
    CString::new(json).unwrap().into_raw()
}
