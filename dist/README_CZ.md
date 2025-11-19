# TravelButton

TravelButton je BepInEx plugin pro Outward (Definitive Edition), který přidá tlačítko pro rychlý teleport na přednastavená města. Plugin podporuje cenu v „silver“, dynamickou konfiguraci a bezpečné načítání scén s překrytím (fade).

## Přidej se na Discord a nahlas chyby nebo mi dej feedback:

Link: <a href="https://discord.gg/rKGzYaH4">https://discord.gg/rKGzYaH4</a>

## Hlavní vlastnosti
- Jedno tlačítko v UI pro otevření dialogu s cíli.
- Konfigurovatelný seznam měst (soubor JSON + BepInEx cfg položky).
- Per-město cena a povolení (enable/disable).
- Pokus o odečtení měny pomocí více fallbacků (inventář, bag, pole).
- Detekce změn konfiguračního souboru na disku a automatická aktualizace UI.
- Bezpečné načítání scény s černým překrytím, aby hráč nic neviděl při přesunu.

## Požadavky
- Outward (Definitive Edition)
- BepInEx 5.x
- (Doporučeno) ConfigurationManager pro snadné úpravy z GUI

## Instalace
1. Vytvořte složku (pokud neexistuje):  
   `<GameRoot>/BepInEx/plugins/TravelButton/`
2. Zkopírujte do ní tyto soubory:
   - TravelButtonMod.dll  
   - TravelButtonMod.pdb  
   - TravelButton_Cities.json  
   - TravelButton_icon.png
3. Spusťte hru. V logu BepInEx by se měly objevit informace o inicializaci pluginu.

## Konfigurace
- Plugin vytvoří BepInEx ConfigEntries:
  - TravelButton.EnableMod
  - TravelButton.GlobalTravelPrice
  - TravelButton.CurrencyItem
  - TravelButton.Cities (pro každé město `{CityName}.Enabled` a `{CityName}.Price`)
- `TravelButton_Cities.json` slouží jako seed s koordináty a výchozími hodnotami. Můžete jej upravit před prvním spuštěním nebo nechat plugin vygenerovat výchozí.

## Použití
- Otevřete herní toolbar/inventory, klikněte na TravelButton.
- V dialogu vyberte cíl. Pokud má cíl cenu, plugin se pokusí odečíst silver před teleportem. Pokud odečet selže, teleport se zruší.

## Rychlé řešení problémů
- Cena se nezměnila po editaci cfg souboru:
  - Zkontrolujte logy BepInEx pro `Config.Reload()` a zprávy pluginu.
- Peníze se neodečítají:
  - Zkontrolujte logy `TryDeductPlayerCurrency`, které ukazují, na které komponentě proběhl pokus o odečet.
  - Plugin nyní provádí skutečný odečet před načtením scény — pokud odečet selže, teleport se zruší.
- FileSystemWatcher posílá duplicitní události — plugin má debounce; zvyšte hodnotu, pokud stále přijde víc eventů.

## Vývojáři
- Kompilace: sestavte do DLL kompatibilní s Unity/Outward (Unity 2020 / .NET 4.x kompatibilita).
- Výstupní DLL umístit do BepInEx/plugins podle instrukcí výše.

## Licence / Kontakt
- Deep, https://discord.gg/rKGzYaH4

```