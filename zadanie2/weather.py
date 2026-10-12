"""Погода по списку городов через wttr.in с группировкой по странам.

Запуск:
    python weather.py                 # список городов берётся из облака (CITIES_URL)
    python weather.py cities.txt      # или из локального файла / другого URL

Используется только стандартная библиотека Python (3.8+).
"""

import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from typing import Dict, List, Optional

CITIES_URL = "https://gistpad.com/raw/vk-task-14"
WEATHER_URL = "https://wttr.in/{city}?format=j1"
TIMEOUT = 30
RETRIES = 3
MAX_WORKERS = 4


@dataclass(frozen=True)
class WeatherData:
    city: str
    country: str
    temperature_c: int

    def __str__(self) -> str:
        return f"{self.city}, {self.country} {format_temp(self.temperature_c)}"


def format_temp(value: float) -> str:
    """+18 °C / -3 °C / 0 °C; дробная часть показывается, только если она есть."""
    value = round(value, 1)
    if value == int(value):
        value = int(value)
    sign = "+" if value > 0 else ""
    return f"{sign}{value} °C"


def http_get(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "curl/8.0"})
    last_error: Optional[Exception] = None
    for attempt in range(1, RETRIES + 1):
        try:
            with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
                return response.read()
        except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
            last_error = error
            if attempt < RETRIES:
                time.sleep(attempt)
    raise RuntimeError(f"не удалось загрузить {url}: {last_error}")


def load_cities(source: str) -> List[str]:
    """Читает список городов (по одному в строке) и возвращает уникальные, сохраняя порядок."""
    if source.startswith(("http://", "https://")):
        text = http_get(source).decode("utf-8-sig")
    else:
        with open(source, encoding="utf-8-sig") as file:
            text = file.read()

    unique: Dict[str, str] = {}
    for line in text.splitlines():
        city = line.strip()
        if city and city.casefold() not in unique:
            unique[city.casefold()] = city
    return list(unique.values())


def fetch_weather(city: str) -> WeatherData:
    url = WEATHER_URL.format(city=urllib.parse.quote(city))
    try:
        data = json.loads(http_get(url).decode("utf-8"))
        temperature = int(data["current_condition"][0]["temp_C"])
        country = data["nearest_area"][0]["country"][0]["value"].strip()
    except (ValueError, KeyError, IndexError, TypeError) as error:
        raise RuntimeError(f"неожиданный ответ wttr.in для '{city}': {error!r}") from error
    return WeatherData(city=city, country=country, temperature_c=temperature)


def group_by_country(items: List[WeatherData]) -> Dict[str, List[WeatherData]]:
    groups: Dict[str, List[WeatherData]] = {}
    for item in items:
        groups.setdefault(item.country, []).append(item)
    return groups


def plural_cities(count: int) -> str:
    return "city" if count == 1 else "cities"


def main() -> int:
    # Чтобы «°» и «—» корректно печатались в консоли Windows.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    source = sys.argv[1] if len(sys.argv) > 1 else CITIES_URL
    cities = load_cities(source)
    if not cities:
        print("Список городов пуст.", file=sys.stderr)
        return 1

    def safe_fetch(city: str) -> Optional[WeatherData]:
        try:
            return fetch_weather(city)
        except RuntimeError as error:
            print(f"[!] {error}", file=sys.stderr)
            return None

    with ThreadPoolExecutor(max_workers=MAX_WORKERS) as pool:
        results = [r for r in pool.map(safe_fetch, cities) if r is not None]

    print("Погода по городам:")
    for item in results:
        print(f"  {item}")

    print("\nПо странам:")
    groups = group_by_country(results)
    for country in sorted(groups):
        temps = [item.temperature_c for item in groups[country]]
        print(
            f"  {country} — {len(temps)} {plural_cities(len(temps))}, "
            f"avg: {format_temp(sum(temps) / len(temps))}, "
            f"min: {format_temp(min(temps))}, "
            f"max: {format_temp(max(temps))}"
        )

    return 0 if len(results) == len(cities) else 2


if __name__ == "__main__":
    sys.exit(main())
