/// Аналог Helpers/TelegramEscaper.cs — байт-в-байт та же последовательность замен.
pub fn escape_markdown_v2(text: &str) -> String {
    let mut s = text.to_string();
    s = s.replace('\\', "\\\\");
    s = s.replace('_', "\\_");
    s = s.replace('*', "\\*");
    s = s.replace('[', "\\[");
    s = s.replace(']', "\\]");
    s = s.replace('(', "\\(");
    s = s.replace(')', "\\)");
    s = s.replace('~', "\\~");
    s = s.replace('`', "\\`");
    s = s.replace('>', "\\>");
    s = s.replace('#', "\\#");
    s = s.replace('+', "\\+");
    s = s.replace('-', "\\-");
    s = s.replace('=', "\\=");
    s = s.replace('|', "\\|");
    s = s.replace('{', "\\{");
    s = s.replace('}', "\\}");
    s = s.replace('.', "\\.");
    s = s.replace('!', "\\!");
    s
}
