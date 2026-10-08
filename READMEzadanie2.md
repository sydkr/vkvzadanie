# Weather by cities (wttr.in)

Скрипт на Python ( `json`, `urllib`, `dataclasses`, `concurrent.futures`).

1. Загружает список городов из облачного файла <https://gistpad.com/raw/vk-task-14> в память
   (убирает пустые строки, пробелы, BOM и дубликаты без учёта регистра).
2. Для каждого уникального города запрашивает `https://wttr.in/{City}?format=j1`.
3. Складывает результат в модель `WeatherData` (`city`, `country`, `temperature_c`).
4. Выводит погоду по каждому городу: `Tokyo, Japan +18 °C`.
5. Группирует по странам и выводит количество городов, среднюю, минимальную и максимальную температуру:
   `Japan — 3 cities, avg: +19 °C, min: +16 °C, max: +22 °C`.

## Запуск

```bash
python weather.py              # список из облака
python weather.py cities.txt   # или из локального файла
```

## Пример вывода

```
Погода по городам:
  Moscow, Russia +9 °C
  Khabarovsk, Russia +4 °C
  Saint-Petersburg, Russia +7 °C
  Vienna, Austria +15 °C
  Izhevsk, Russia +6 °C
  Perm, Russia +7 °C
  NhaTrang, Vietnam +25 °C
  Villach, Austria +11 °C

По странам:
  Austria — 2 cities, avg: +13 °C, min: +11 °C, max: +15 °C
  Russia — 5 cities, avg: +6.6 °C, min: +4 °C, max: +9 °C
  Vietnam — 1 city, avg: +25 °C, min: +25 °C, max: +25 °C
```

Город,который не удалось получить, выводится в stderr с пометкой `!` и не попадает в статистику (код выхода 2).
