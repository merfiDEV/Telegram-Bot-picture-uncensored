use chrono::{Datelike, Local, NaiveDate};

#[test]
fn parse_dd_mm_with_current_year() {
    let year = Local::now().year();
    let dt = NaiveDate::from_ymd_opt(year, 10, 13).unwrap();
    assert_eq!("13.10", dt.format("%d.%m").to_string());
    assert_eq!(10, dt.month());
    assert_eq!(13, dt.day());
}
