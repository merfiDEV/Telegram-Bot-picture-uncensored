use xzbot::helpers::telegram_escaper::escape_markdown_v2;

#[test]
fn empty_string_returns_empty() {
    assert_eq!("", escape_markdown_v2(""));
}

#[test]
fn plain_text_unchanged() {
    assert_eq!("hello world", escape_markdown_v2("hello world"));
}

#[test]
fn special_chars_escaped() {
    assert_eq!("\\_", escape_markdown_v2("_"));
    assert_eq!("\\*", escape_markdown_v2("*"));
    assert_eq!("\\[", escape_markdown_v2("["));
    assert_eq!("\\]", escape_markdown_v2("]"));
    assert_eq!("\\(", escape_markdown_v2("("));
    assert_eq!("\\)", escape_markdown_v2(")"));
    assert_eq!("\\~", escape_markdown_v2("~"));
    assert_eq!("\\>", escape_markdown_v2(">"));
    assert_eq!("\\#", escape_markdown_v2("#"));
    assert_eq!("\\+", escape_markdown_v2("+"));
    assert_eq!("\\-", escape_markdown_v2("-"));
    assert_eq!("\\=", escape_markdown_v2("="));
    assert_eq!("\\|", escape_markdown_v2("|"));
    assert_eq!("\\{", escape_markdown_v2("{"));
    assert_eq!("\\}", escape_markdown_v2("}"));
    assert_eq!("\\.", escape_markdown_v2("."));
    assert_eq!("\\!", escape_markdown_v2("!"));
}

#[test]
fn backslash_escaped_first() {
    assert_eq!("\\\\", escape_markdown_v2("\\"));
}

#[test]
fn mixed_text_all_escaped() {
    assert_eq!("Hello\\_World\\!", escape_markdown_v2("Hello_World!"));
}
