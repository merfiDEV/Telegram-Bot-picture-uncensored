use crate::helpers::telegram_escaper::escape_markdown_v2;
use crate::models::bot_state::{BotState, DashboardState, RequestRecord, SharedState};
use chrono::{Datelike, Local, NaiveDate};
use skia_safe::{
    Color, EncodedImageFormat, Font, FontMgr, FontStyle, Paint, PaintCap, PaintStyle, Path, Point,
    Shader, TileMode,
};
use std::collections::HashMap;

// Backslash и скобки собираются из кодов, чтобы не зависеть от экранирования в исходнике.
const BS: char = '\u{5c}';
const LP: char = '(';
const RP: char = ')';
const QUOTE: char = '\u{22}';

fn esc_lp() -> String {
    format!("{}{}", BS, LP)
}
fn esc_rp() -> String {
    format!("{}{}", BS, RP)
}

/// Аналог Services/BotStatsService.cs.
pub struct BotStatsService {
    state: SharedState,
}

impl BotStatsService {
    pub fn new(state: SharedState) -> Self {
        BotStatsService { state }
    }

    pub fn generate_chart_image(&self) -> Vec<u8> {
        let (daily_dict, recent) = {
            let st = self.state.lock().unwrap();
            (st.daily_usage.clone(), st.recent_requests.clone())
        };

        let mut daily = daily_dict;
        if daily.is_empty() && !recent.is_empty() {
            let mut map: HashMap<String, i32> = HashMap::new();
            for r in &recent {
                let key = r.time.format("%d.%m").to_string();
                *map.entry(key).or_insert(0) += 1;
            }
            daily = map;
        }

        // Ключи вида "dd.MM" без года — как в C# DateTime.TryParseExact с текущим годом.
        let current_year = Local::now().year();
        let mut points_list: Vec<(NaiveDate, i32)> = Vec::new();
        for (k, v) in &daily {
            let parts: Vec<&str> = k.split('.').collect();
            if parts.len() == 2 {
                if let (Ok(day), Ok(month)) = (parts[0].parse::<u32>(), parts[1].parse::<u32>()) {
                    if let Some(dt) = NaiveDate::from_ymd_opt(current_year, month, day) {
                        points_list.push((dt, *v));
                        continue;
                    }
                }
            }
            crate::log!(
                "BotStats: пропущен ключ DailyUsage с некорректным форматом даты: '{}'",
                k
            );
        }
        points_list.sort_by_key(|x| x.0);

        let sorted_stats: Vec<(String, i32)> = points_list
            .iter()
            .map(|(d, v)| (d.format("%d.%m").to_string(), *v))
            .collect();

        if sorted_stats.is_empty() {
            return create_placeholder_image("Нет данных");
        }

        let width = 700;
        let height = 260;
        let mut surface = match skia_safe::surfaces::raster_n32_premul((width, height)) {
            Some(s) => s,
            None => return create_placeholder_image("Ошибка"),
        };

        let bg = Color::from_rgb(11, 18, 32);
        let grid = Color::from_argb(160, 60, 75, 90);
        let line_color = Color::from_rgb(33, 150, 243);
        let fill_start = Color::from_argb(120, 33, 150, 243);

        let left = 60.0f32;
        let right = 20.0f32;
        let top = 20.0f32;
        let bottom = 50.0f32;
        let plot_w = width as f32 - left - right;
        let plot_h = height as f32 - top - bottom;

        let mut max_val = sorted_stats.iter().map(|x| x.1).max().unwrap_or(0);
        if max_val == 0 {
            max_val = 1;
        }

        {
            let canvas: &skia_safe::Canvas = surface.canvas();
            canvas.clear(bg);

            let mut grid_paint = Paint::default();
            grid_paint.set_color(grid);
            grid_paint.set_stroke_width(1.0);
            grid_paint.set_anti_alias(true);

            let mut label_paint = Paint::default();
            label_paint.set_color(Color::LIGHT_GRAY);
            label_paint.set_anti_alias(true);

            let font = default_font(12.0);

            let y_ticks = 4;
            for i in 0..=y_ticks {
                let yy = top + (plot_h * i as f32 / y_ticks as f32);
                canvas.draw_line((left, yy), (left + plot_w, yy), &grid_paint);
                let value = (max_val as f32 * (1.0 - i as f32 / y_ticks as f32)).round() as i32;
                canvas.draw_str(value.to_string(), (8.0, yy + 5.0), &font, &label_paint);
            }
        }

        let count = sorted_stats.len();
        let x_step = plot_w / (if count > 1 { count - 1 } else { 1 }) as f32;
        let mut points: Vec<Point> = Vec::new();
        for i in 0..count {
            let x = left + i as f32 * x_step;
            let y = top + (plot_h - (sorted_stats[i].1 as f32 / max_val as f32 * plot_h));
            points.push(Point::new(x, y));
        }

        {
            let canvas: &skia_safe::Canvas = surface.canvas();

            let mut fill_paint = Paint::default();
            fill_paint.set_anti_alias(true);
            fill_paint.set_style(PaintStyle::Fill);
            let shader = Shader::linear_gradient(
                (Point::new(0.0, top), Point::new(0.0, top + plot_h)),
                [fill_start, Color::TRANSPARENT].as_ref(),
                None,
                TileMode::Clamp,
                None,
                None,
            );
            if let Some(sh) = shader {
                fill_paint.set_shader(sh);
            }

            let mut fill_path = Path::new();
            fill_path.move_to(Point::new(points[0].x, top + plot_h));
            for p in &points {
                fill_path.line_to(*p);
            }
            fill_path.line_to(Point::new(points.last().unwrap().x, top + plot_h));
            fill_path.close();
            canvas.draw_path(&fill_path, &fill_paint);

            let mut shadow_paint = Paint::default();
            shadow_paint.set_anti_alias(true);
            shadow_paint.set_color(Color::BLACK.with_a(90));
            shadow_paint.set_stroke_width(8.0);
            shadow_paint.set_style(PaintStyle::Stroke);
            shadow_paint.set_stroke_cap(PaintCap::Round);

            let mut path = Path::new();
            path.move_to(points[0]);
            for p in points.iter().skip(1) {
                path.line_to(*p);
            }
            canvas.draw_path(&path, &shadow_paint);

            let mut line_paint = Paint::default();
            line_paint.set_anti_alias(true);
            line_paint.set_color(line_color);
            line_paint.set_stroke_width(3.0);
            line_paint.set_style(PaintStyle::Stroke);
            line_paint.set_stroke_cap(PaintCap::Round);
            canvas.draw_path(&path, &line_paint);

            let mut dot_fill = Paint::default();
            dot_fill.set_anti_alias(true);
            dot_fill.set_color(Color::WHITE);
            dot_fill.set_style(PaintStyle::Fill);

            let mut dot_stroke = Paint::default();
            dot_stroke.set_anti_alias(true);
            dot_stroke.set_color(line_color);
            dot_stroke.set_stroke_width(2.0);
            dot_stroke.set_style(PaintStyle::Stroke);

            let mut halo = Paint::default();
            halo.set_anti_alias(true);
            halo.set_color(line_color.with_a(60));
            halo.set_style(PaintStyle::Fill);

            for (i, p) in points.iter().enumerate() {
                let r = if i == points.len() - 1 { 5.0 } else { 3.5 };
                canvas.draw_circle(*p, r + 3.0, &halo);
                canvas.draw_circle(*p, r, &dot_fill);
                canvas.draw_circle(*p, r, &dot_stroke);
            }

            let mut x_label_paint = Paint::default();
            x_label_paint.set_color(Color::LIGHT_GRAY);
            x_label_paint.set_anti_alias(true);
            let x_font = default_font(12.0);

            let max_labels = sorted_stats.len().min(7);
            let step = (sorted_stats.len() / max_labels).max(1);
            let mut i = 0;
            while i < sorted_stats.len() {
                let p = points[i];
                let label = &sorted_stats[i].0;
                let (text_w, _) = x_font.measure_str(label, Some(&x_label_paint));
                canvas.draw_str(
                    label,
                    (p.x - text_w / 2.0, top + plot_h + 20.0),
                    &x_font,
                    &x_label_paint,
                );
                i += step;
            }
        }

        let image = surface.image_snapshot();
        match image.encode(None, EncodedImageFormat::PNG, 100) {
            Some(data) => data.as_bytes().to_vec(),
            None => create_placeholder_image("Ошибка"),
        }
    }

    pub fn increment_usage(&self) {
        let mut st = self.state.lock().unwrap();
        st.usage_count += 1;
        let date = Local::now().format("%d.%m").to_string();
        *st.daily_usage.entry(date).or_insert(0) += 1;
    }

    pub fn record_response_time(&self, elapsed: chrono::Duration) {
        let mut st = self.state.lock().unwrap();
        st.response_times_ms
            .push((elapsed.num_milliseconds() as f64 * 10.0).round() / 10.0);
        if st.response_times_ms.len() > 1000 {
            let remove = st.response_times_ms.len() - 1000;
            st.response_times_ms.drain(0..remove);
        }
    }

    pub fn record_response_time_provider(&self, provider_key: &str, elapsed: chrono::Duration) {
        if provider_key.trim().is_empty() {
            return;
        }
        let key = provider_key.trim().to_lowercase();
        let mut st = self.state.lock().unwrap();
        let list = st
            .response_times_by_provider
            .entry(key)
            .or_insert_with(Vec::new);
        list.push((elapsed.num_milliseconds() as f64 * 10.0).round() / 10.0);
        if list.len() > 1000 {
            let remove = list.len() - 1000;
            list.drain(0..remove);
        }
    }

    pub fn record_error(&self, error_type: &str) {
        let mut st = self.state.lock().unwrap();
        st.error_count += 1;
        *st.error_details.entry(error_type.to_string()).or_insert(0) += 1;

        if st.error_details.len() > 50 {
            let mut entries: Vec<(String, i32)> = st
                .error_details
                .iter()
                .map(|(k, v)| (k.clone(), *v))
                .collect();
            entries.sort_by(|a, b| b.1.cmp(&a.1));
            entries.truncate(50);
            st.error_details = entries.into_iter().collect();
        }
    }

    pub fn record_request(&self, user_id: i64, username: Option<&str>, query: &str, success: bool) {
        let now = Local::now();
        {
            let mut st = self.state.lock().unwrap();
            st.recent_requests.insert(
                0,
                RequestRecord {
                    time: now,
                    user_id,
                    username: if username.unwrap_or("").trim().is_empty() {
                        "Unknown".to_string()
                    } else {
                        username.unwrap().to_string()
                    },
                    query: query.to_string(),
                    success,
                },
            );
            if st.recent_requests.len() > 200 {
                st.recent_requests.truncate(200);
            }
            update_stats_incremental(&mut st, query, success);
        }
        let st = self.state.lock().unwrap();
        st.save();
    }

    pub fn build_stats_text(&self, provider_statuses: &[(String, String, bool, String)]) -> String {
        let st = self.state.lock().unwrap();
        let uptime = Local::now() - st.started_at;
        let uptime_str = format_uptime(uptime);
        let started_at = st.started_at.format("%d.%m.%Y %H:%M:%S").to_string();
        let success_count = (st.usage_count - st.error_count).max(0);
        let success_rate = if st.usage_count > 0 {
            (success_count as f64 / st.usage_count as f64 * 100.0 * 10.0).round() / 10.0
        } else {
            100.0
        };

        let mut services = String::new();
        for (_key, display_name, ok, status) in provider_statuses {
            let icon = if *ok { "✅" } else { "❌" };
            services.push_str(&format!(
                "  {}: {} `{}`
",
                escape_markdown_v2(display_name),
                icon,
                escape_markdown_v2(status)
            ));
        }

        let lp = esc_lp();
        let rp = esc_rp();
        let mut s = String::new();
        s.push_str(
            "📊 *Статистика бота*

",
        );
        s.push_str(
            "⏱ *Аптайм*
",
        );
        s.push_str(&format!(
            "  `{}` {}{}с `{}`{}

",
            escape_markdown_v2(&uptime_str),
            lp,
            "",
            escape_markdown_v2(&started_at),
            rp
        ));
        s.push_str(
            "🌐 *Внешние сервисы*
",
        );
        s.push_str(&services);
        s.push_str(
            "
📈 *Запросы*
",
        );
        s.push_str(&format!(
            "  Всего: `{}`
",
            st.usage_count
        ));
        s.push_str(&format!(
            "  Успешных: `{}` {}{}%{}
",
            success_count,
            lp,
            escape_markdown_v2(&success_rate.to_string()),
            rp
        ));
        s.push_str(&format!(
            "  Ошибок: `{}`

",
            st.error_count
        ));
        s.push_str("🔐 _admin only_");
        s
    }

    pub fn build_metrics_text(&self) -> String {
        let st = self.state.lock().unwrap();
        let uptime = Local::now() - st.started_at;
        let requests_per_minute = if uptime.num_seconds() > 0 {
            (st.usage_count as f64 / (uptime.num_seconds() as f64 / 60.0) * 100.0).round() / 100.0
        } else {
            0.0
        };

        let mut lines = vec!["📈 *Метрики производительности*".to_string(), String::new()];

        for provider_key in ["bing", "ddg"] {
            let provider_name = if provider_key == "ddg" {
                "DuckDuckGo"
            } else {
                "Bing"
            };
            let times = st.response_times_by_provider.get(provider_key);

            match times {
                Some(t) if !t.is_empty() => {
                    let avg = t.iter().sum::<f64>() / t.len() as f64;
                    let avg = (avg * 10.0).round() / 10.0;
                    let min = t.iter().cloned().fold(f64::INFINITY, f64::min);
                    let max = t.iter().cloned().fold(f64::NEG_INFINITY, f64::max);
                    lines.push(format!("⏱ *Время ответа {}*", provider_name));
                    lines.push(format!(
                        "  среднее:  `{} мс`",
                        escape_markdown_v2(&avg.to_string())
                    ));
                    lines.push(format!(
                        "  мин:      `{} мс`",
                        escape_markdown_v2(&min.to_string())
                    ));
                    lines.push(format!(
                        "  макс:     `{} мс`",
                        escape_markdown_v2(&max.to_string())
                    ));
                    lines.push(format!("  замеров:  `{}`", t.len()));
                    lines.push(String::new());
                }
                _ => {
                    lines.push(format!("⏱ *Время ответа {}:* нет данных", provider_name));
                    lines.push(String::new());
                }
            }
        }

        lines.push(format!(
            "🔢 *Нагрузка:* `{}` зап/мин",
            escape_markdown_v2(&requests_per_minute.to_string())
        ));
        lines.push(String::new());

        if !st.error_details.is_empty() {
            lines.push("⚠️ *Ошибки по типам:*".to_string());
            let mut entries: Vec<(&String, &i32)> = st.error_details.iter().collect();
            entries.sort_by(|a, b| b.1.cmp(a.1));
            for (k, v) in entries {
                lines.push(format!("  `{}` — `{}`", escape_markdown_v2(k), v));
            }
        } else {
            lines.push("✅ *Ошибок не зафиксировано*".to_string());
        }

        lines.join(
            "
",
        )
    }

    pub fn build_dashboard_text(
        &self,
        user_id: i64,
        page: Option<i32>,
        filter: Option<&str>,
        search: Option<&str>,
    ) -> String {
        let mut st = self.state.lock().unwrap();

        if !st.dashboard_states.contains_key(&user_id) {
            st.dashboard_states
                .insert(user_id, DashboardState::default());
        }

        let mut cur_page;
        let mut cur_filter;
        let mut cur_search;
        {
            let ds = st.dashboard_states.get(&user_id).unwrap();
            cur_page = ds.page;
            cur_filter = ds.filter.clone();
            cur_search = ds.search.clone();
        }
        if let Some(p) = page {
            cur_page = p;
        }
        if let Some(f) = filter {
            cur_filter = f.to_string();
        }
        if let Some(s) = search {
            cur_search = s.to_string();
        }
        {
            let ds = st.dashboard_states.get_mut(&user_id).unwrap();
            ds.page = cur_page;
            ds.filter = cur_filter.clone();
            ds.search = cur_search.clone();
        }

        let mut lines = vec!["📋 *Дашборд последних запросов*".to_string(), String::new()];

        let today = Local::now().date_naive();
        let today_requests: Vec<&RequestRecord> = st
            .recent_requests
            .iter()
            .filter(|r| r.time.date_naive() == today)
            .collect();
        let today_total = today_requests.len();
        let today_success = today_requests.iter().filter(|r| r.success).count();
        let today_errors = today_requests.iter().filter(|r| !r.success).count();

        let mut top_map: HashMap<String, i32> = HashMap::new();
        for r in &today_requests {
            *top_map.entry(r.query.clone()).or_insert(0) += 1;
        }
        let mut top_queries: Vec<(String, i32)> = top_map.into_iter().collect();
        top_queries.sort_by(|a, b| b.1.cmp(&a.1));
        top_queries.truncate(5);

        lines.push(format!(
            "📊 *За сегодня:* `{}` запросов ✅ `{}` ❌ `{}`",
            today_total, today_success, today_errors
        ));
        if !top_queries.is_empty() {
            let q = QUOTE;
            let lp = esc_lp();
            let rp = esc_rp();
            let top_str = top_queries
                .iter()
                .map(|(qq, c)| {
                    format!(
                        "{}{}{} {}{}{}",
                        q,
                        escape_markdown_v2(qq.trim()),
                        q,
                        lp,
                        c,
                        rp
                    )
                })
                .collect::<Vec<_>>()
                .join(", ");
            lines.push(format!("🔥 *Топ:* {}", top_str));
        }
        lines.push(String::new());

        let mut filtered: Vec<RequestRecord> = st.recent_requests.iter().cloned().collect();
        if cur_filter == "success" {
            filtered.retain(|r| r.success);
        } else if cur_filter == "errors" {
            filtered.retain(|r| !r.success);
        }
        if !cur_search.is_empty() {
            let needle = cur_search.to_lowercase();
            filtered.retain(|r| r.query.to_lowercase().contains(&needle));
        }

        let list = filtered;
        if list.is_empty() {
            lines.push("  _Нет записей по текущему фильтру_".to_string());
            lines.push(String::new());
            lines.push(format!("Страница {}/1", cur_page + 1));
            return lines.join(
                "
",
            );
        }

        let page_size = 10;
        let total_pages = ((list.len() as f64) / page_size as f64).ceil() as i32;
        if cur_page >= total_pages {
            cur_page = total_pages - 1;
        }
        if cur_page < 0 {
            cur_page = 0;
        }
        {
            let ds = st.dashboard_states.get_mut(&user_id).unwrap();
            ds.page = cur_page;
        }

        let start = (cur_page * page_size) as usize;
        let paged: Vec<RequestRecord> = list
            .iter()
            .skip(start)
            .take(page_size as usize)
            .cloned()
            .collect();

        let lp = esc_lp();
        let rp = esc_rp();
        lines.push(format!(
            "📄 *Страница {}/{}* {}{}всего {} записей{}",
            cur_page + 1,
            total_pages,
            lp,
            "",
            list.len(),
            rp
        ));
        lines.push(String::new());

        for request in paged {
            let time = request.time.format("%H:%M:%S").to_string();
            let status = if request.success { "✅" } else { "❌" };
            let query = if request.query.chars().count() > 25 {
                let truncated: String = request.query.chars().take(25).collect();
                format!("{}...", truncated)
            } else {
                request.query.clone()
            };
            lines.push(format!(
                "`[{}]` {} `@{}` {}{}`{}`{}: _{}_",
                escape_markdown_v2(&time),
                status,
                escape_markdown_v2(&request.username),
                lp,
                "",
                request.user_id,
                rp,
                escape_markdown_v2(&query)
            ));
        }

        lines.join(
            "
",
        )
    }

    pub fn dashboard_state(&self, user_id: i64) -> DashboardState {
        let mut st = self.state.lock().unwrap();
        if !st.dashboard_states.contains_key(&user_id) {
            st.dashboard_states
                .insert(user_id, DashboardState::default());
        }
        st.dashboard_states.get(&user_id).unwrap().clone()
    }

    pub fn set_dashboard_awaiting_search(&self, user_id: i64, awaiting: bool) {
        let mut st = self.state.lock().unwrap();
        if !st.dashboard_states.contains_key(&user_id) {
            st.dashboard_states
                .insert(user_id, DashboardState::default());
        }
        st.dashboard_states
            .get_mut(&user_id)
            .unwrap()
            .awaiting_search = awaiting;
        st.save();
    }
}

fn default_font(size: f32) -> Font {
    let mgr = FontMgr::default();
    match mgr.legacy_make_typeface(None, FontStyle::normal()) {
        Some(typeface) => Font::new(typeface, Some(size)),
        None => Font::default(),
    }
}

fn update_stats_incremental(st: &mut BotState, query: &str, success: bool) {
    st.today_requests += 1;
    if success {
        st.today_success += 1;
    } else {
        st.today_errors += 1;
    }

    if !query.is_empty() {
        let entry = st.popular_queries.entry(query.to_string()).or_insert(0);
        *entry += 1;
        if st.popular_queries.len() > 50 {
            let mut entries: Vec<(String, i32)> = st
                .popular_queries
                .iter()
                .map(|(k, v)| (k.clone(), *v))
                .collect();
            entries.sort_by(|a, b| b.1.cmp(&a.1));
            entries.truncate(50);
            st.popular_queries = entries.into_iter().collect();
        }
    }
}

fn format_uptime(uptime: chrono::Duration) -> String {
    let mut parts = Vec::new();
    let secs = uptime.num_seconds();
    let days = secs / 86400;
    let hours = (secs % 86400) / 3600;
    let minutes = (secs % 3600) / 60;
    let seconds = secs % 60;
    if days > 0 {
        parts.push(format!("{}д", days));
    }
    if hours > 0 {
        parts.push(format!("{}ч", hours));
    }
    if minutes > 0 {
        parts.push(format!("{}м", minutes));
    }
    parts.push(format!("{}с", seconds));
    parts.join(" ")
}

fn create_placeholder_image(text: &str) -> Vec<u8> {
    let width = 400;
    let height = 120;
    let mut surface = match skia_safe::surfaces::raster_n32_premul((width, height)) {
        Some(s) => s,
        None => return Vec::new(),
    };

    let bg = Color::from_rgb(24, 32, 48);
    {
        let canvas: &skia_safe::Canvas = surface.canvas();
        canvas.clear(bg);

        let mut paint = Paint::default();
        paint.set_color(Color::LIGHT_GRAY);
        paint.set_anti_alias(true);
        let font = default_font(20.0);
        let (text_width, _) = font.measure_str(text, Some(&paint));
        canvas.draw_str(
            text,
            (
                (width as f32 - text_width) / 2.0,
                height as f32 / 2.0 + 10.0,
            ),
            &font,
            &paint,
        );
    }

    let image = surface.image_snapshot();
    match image.encode(None, EncodedImageFormat::PNG, 90) {
        Some(data) => data.as_bytes().to_vec(),
        None => Vec::new(),
    }
}
