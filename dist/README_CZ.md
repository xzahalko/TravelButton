```markdown
# TravelButton

Mod pro Outward (definitive edition) — přidává do hry tlačítko pro cestování s konfigurací pro jednotlivá města (cena, povolit, sledování navštívení).

Tento README je krátký a zaměřený na požadavky instalace pro tři artefakty pluginu, které jste poskytl, a na popis chování modu za běhu.

---

## Požadované soubory
Umístěte tyto soubory společně do stejné složky pluginu (viz Instalace):

- `TravelButton.dll` — požadovaná sestava pluginu (kód modu).
- `TravelButton.pdb` — volitelné symboly ladění (umístěte vedle DLL pro podrobnější zásobníky ve výpisech).
- `TravelButton_Cities.json` — kanonický soubor s daty měst (seedovaná metadata měst, příznaky visited). Plugin zde standardně čte a ukládá data, pokud je JSON umístěn vedle DLL.

> Důležité: plugin detekuje svou složku podle umístění DLL. Umístění JSON vedle DLL zajistí předvídatelné chování při načítání/ukládání.

---

## Požadavky
- Nainstalovaný a funkční BepInEx pro vaši verzi hry (běžně BepInEx 5+ pro moderní Unity hry).
- Hra musí být spuštěna s BepInEx, aby se pluginy načetly.
- (Volitelné) ConfigurationManager nebo podobné in‑game rozhraní pro úpravu nastavení přímo ve hře.

---

## Instalace (ručně)
1. Najděte kořenovou složku hry (kde je spustitelný soubor hry).
2. Ujistěte se, že je BepInEx nainstalovaný (měl by být dostupný adresář `BepInEx`).
3. Vytvořte složku pro plugin:
   - `<game root>\BepInEx\plugins\TravelButton\`
4. Zkopírujte soubory:
   - `TravelButton.dll` → `<game root>\BepInEx\plugins\TravelButton\TravelButton.dll`
   - `TravelButton.pdb` (volitelné) → stejná složka
   - `TravelButton_Cities.json` → stejná složka
5. Spusťte (nebo restartujte) hru.
6. Zkontrolujte logy BepInEx (např. `BepInEx/LogOutput.log`) na zprávy o spuštění TravelButton.

---

## Chování (podrobně)

Tato část vysvětluje, jak mod spravuje města, konfiguraci a stav „visited“.

### Data měst
- `TravelButton_Cities.json` obsahuje seedovaná záznamy měst. Typická pole pro každé město:
  - `name` (string) — jedinečný identifikátor města zobrazený v UI
  - `sceneName` (volitelný string)
  - `targetGameObjectName` (volitelný string)
  - `coords` (volitelné pole [x,y,z])
  - `price` (volitelné celé číslo)
  - `visited` (volitelný bool seed)
- Pokud JSON obsahuje `price`, tato hodnota se použije jako výchozí pro BepInEx ConfigEntry; za běhu jsou však autoritativní hodnoty z BepInEx (ConfigEntries).

### Konfigurace pro jednotlivá města
- Pro každé město plugin vytváří vazby konfigurace:
  - `Enabled` (bool) — zda je možné do města cestovat (ovlivňuje dostupnost).
  - `Price` (int) — cena teleportu.
  - `Visited` (bool) — zda je město považováno za navštívené.
- Plugin váže BepInEx ConfigEntries (`Config.Bind`) pro tyto klíče, takže je ConfigurationManager (pokud je přítomen) může zobrazit ve hře.
- Handlery `SettingChanged` synchronizují změny z ConfigEntries zpět do běhového modelu města a persistují je.

!!!UPOZORNĚNÍ!!!: neupravujte příznak `Visited` přes konfiguraci. Mod jej sleduje a označí při skutečné návštěvě města. Pokud přepíšete hodnotu `Visited` na true manuálně, postup hry může po teleportaci stát nekonzistentním.

### Stav „Visited“ — jak funguje a kde se ukládá
- Účel: „Visited“ označuje, že hráč již dané město navštívil. UI to používá k povolení/zakázání tlačítek pro cestování nebo k zobrazení stavu objevení.
- Persistování:
  - Primární: `TravelButton_Cities.json` — preferované kanonické úložiště. Plugin při persistenci sloučí běhové příznaky `visited` do tohoto JSONu.
  - Legacy: pomocí pomocného BepInEx‑stylu `.cfg` (např. `cz.valheimskal.travelbutton.cfg`) může plugin zapisovat pod `[TravelButton.Cities]` klíče jako `CityName.Visited = true` pro kompatibilitu.

### Jak se `visited` nastavuje za běhu
- Plugin nastaví `visited` v těchto případech:
  - Když se načte scéna, která odpovídá poli `sceneName` nebo `targetGameObjectName` města (automatická detekce), plugin označí město jako navštívené.
  - Po úspěšném teleportu do města plugin označí město jako navštívené.
- Po označení jako visited plugin:
  - Persistuje příznak do `TravelButton_Cities.json`.
  - Volitelně zapíše legacy `.cfg` klíč `CityName.Visited`, pokud je to nastaveno.
  - Obnoví/informuje in‑game UI, aby tlačítka odrážela nový stav.

---

## Úprava dat
- Chcete‑li přidat nebo upravit města, editujte `TravelButton_Cities.json` (zálohujte před úpravou). /* neotestováno */
- Po úpravě JSONu restartujte hru, aby se změny načetly.

---

## Odstranění problémů (Troubleshooting)
- Plugin se nenačítá → ujistěte se, že `TravelButton.dll` je ve správné složce pluginu a že BepInEx je nainstalovaný. Zkontrolujte `BepInEx/LogOutput.log`.
- Změny se neprojeví → ověřte, že `TravelButton_Cities.json` je platný JSON a nachází se vedle DLL.

---

## Odinstalace
- Odstraňte `TravelButton.dll` (a volitelně `TravelButton.pdb`) z:
  - `<game root>\BepInEx\plugins\TravelButton\`
- Volitelně odstraňte `TravelButton_Cities.json`, pokud chcete smazat perzistentní data měst.

---
## Podpora
- Při hlášení chyb přiložte `BepInEx/LogOutput.log` a váš `TravelButton_Cities.json`.

---
## Kontakt
- Autor: Deep

## Poznámky k vydání:

### v1.1.1
* corrected teleportation to Cierzo from Cherson
* revision of target coordination computing

### v1.1.0
* kompletně přepracovaná logika teleportace
* zakázán teleport do Sirocca, protože teleportace tam by mohla narušit postup budování ve městě
* revize kódu

### v1.0.1
* oprava: nově vytvořený cfg soubor se vytvářel s enabled stavy měst

### v1.0.0
* inicializace
```