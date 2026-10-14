use regex::Regex;

/// Регресс-тест: turl должен извлекаться из блока Bing, а не матчиться на "cturl".
#[test]
fn turl_extracted_not_cturl() {
    let turl_re = Regex::new(r#"[^a-zA-Z]turl&quot;:&quot;(.*?)&quot;"#).unwrap();

    // Блок с cturl (пустым) ПЕРЕД turl — раньше это ломало извлечение.
    let block = "{&quot;cturl&quot;:&quot;&quot;,&quot;murl&quot;:&quot;https://cdn.example.com/full.jpg&quot;,&quot;turl&quot;:&quot;https://ts2.mm.bing.net/th?id=THUMB&amp;pid=15.1&quot;}";
    let cap = turl_re.captures(block).expect("turl должен найтись");
    assert_eq!(&cap[1], "https://ts2.mm.bing.net/th?id=THUMB&amp;pid=15.1");

    // Блок без turl — не должно быть матча.
    let block2 = "{&quot;cturl&quot;:&quot;&quot;,&quot;murl&quot;:&quot;x&quot;}";
    assert!(turl_re.captures(block2).is_none());
}
