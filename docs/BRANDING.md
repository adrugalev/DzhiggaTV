# Логотип DzhiggaTV

Новый знак создан встроенным GPT Image по запросу пользователя. Исходный PNG с прозрачностью сохранён в `src/DzhiggaTV.App/Assets/DzhiggaTV-logo.png`. Он используется рядом с названием приложения. ICO из того же изображения содержит размеры 16, 20, 24, 32, 40, 48, 64, 128 и 256 пикселей и используется в EXE, окнах и на панели задач. При конвертации менялись только размер и формат, исходная графика сохранена.

## Промпт

Use case: logo-brand. Asset type: production Windows application icon and header logo for DzhiggaTV, a refined modern IPTV player. Create ONE finished square app icon, not a mockup or presentation sheet. A distinctive bold geometric capital D / television-screen symbol formed by a continuous rounded ribbon, with a crisp right-pointing play triangle as negative space. Teal and aqua with a restrained electric-blue edge, matching a dark charcoal software interface. The mark should feel premium, precise and memorable rather than a generic play button. Use simple substantial forms, excellent optical balance and strong contrast; recognisable at 16, 24, 32 and 48 pixels. A subtle polished gradient is welcome, but keep the silhouette sharp and minimal. Center the mark on a deep navy rounded-square tile filling roughly 90% of the canvas, with even optical margins. Transparent pixels outside the rounded square. Front-facing, flat orthographic logo artwork, no scene, no perspective, no text, no wordmark, no letters other than the abstract D shape, no tiny details, no decorative rays, no broadcast antenna, no watermark. Output a single 1024x1024 icon with real transparency around the tile.

## Оформление окна

Цвета системного заголовка задаются через штатные атрибуты Windows 11 DWM и следуют теме плеера: [документация Microsoft](https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute). Сохраняются стандартные кнопки окна, перетаскивание, изменение размеров и привязка к краям экрана.
