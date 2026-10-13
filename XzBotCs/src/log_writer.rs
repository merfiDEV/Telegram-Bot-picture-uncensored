use chrono::Local;
use std::fs::{File, OpenOptions};
use std::io::Write;
use std::sync::{Mutex, OnceLock};

/// Глобальный логгер: пишет строки с префиксом даты одновременно в stdout и в файл.
/// Аналог C# SetupLogging + TeeTextWriter.
static LOGGER: OnceLock<Mutex<File>> = OnceLock::new();

pub fn init_logging() {
    let logs_dir = std::path::Path::new("..").join("logs");
    let _ = std::fs::create_dir_all(&logs_dir);
    let filename = format!("logs_{}.txt", Local::now().format("%Y%m%d_%H%M%S"));
    let log_path = logs_dir.join(filename);
    if let Ok(file) = OpenOptions::new().append(true).create(true).open(&log_path) {
        let _ = LOGGER.set(Mutex::new(file));
    }
    log_line(&format!("logging started: {}", log_path.display()));
}

/// Записать строку в лог (stdout + файл).
pub fn log_line(value: &str) {
    let line = format!("{} | {}", Local::now().format("%Y-%m-%d %H:%M:%S"), value);
    println!("{}", line);
    if let Some(lock) = LOGGER.get() {
        if let Ok(mut f) = lock.lock() {
            let _ = writeln!(f, "{}", line);
        }
    }
}

/// Макрос: log!("...", args) — пишет через log_line с форматированием.
#[macro_export]
macro_rules! log {
    ($($arg:tt)*) => {
        $crate::log_writer::log_line(&format!($($arg)*))
    };
}
