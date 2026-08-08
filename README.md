# NinjaTrader 8.1 Strategien — Backtest-Projekt

Repo für NinjaScript-Strategien, die im NinjaTrader 8.1 Strategy Analyzer gebacktestet werden.

## Strategien

| Datei | Name | Idee |
|---|---|---|
| `Strategies/OpeningPullback2R.cs` | OpeningPullback2R | Opening-Bias aus den ersten 5 1-Min-Kerzen vs. 9:00-Open, Einstieg auf ersten Pullback, Stop an Fensterstruktur, Ziel = 2R |
| `Strategies/OpeningImmediate2R.cs` | OpeningImmediate2R | Wie oben, aber SOFORTIGER Einstieg direkt nach der 5. Kerze — kein Warten auf einen Pullback |

---

## OpeningPullback2R — Regelwerk

Instrument-Ziel: **FDXS 09-26** (Micro-DAX, Eurex). 1-Minuten-Kerzen.

1. **Referenz:** Open der 09:00-Kerze (Xetra-Eröffnung).
2. **Beobachtung:** Kerzen 09:00–09:05 (5 Stück). Kein Handel in diesem Fenster.
3. **Bias:** Close der 5. Kerze **über** dem 9:00-Open → Long-Bias. **Darunter** → Short-Bias. Exakt gleich → kein Trade.
4. **Einstieg Long:** erste **rote** Kerze nach dem Fenster → Market-Einstieg bei deren Schluss (frühester Fill 09:06:00). **Short:** erste **grüne** Kerze, spiegelbildlich.
5. **Stop Long:** tiefster **Close einer roten Kerze** aus den 5 Anfangskerzen, minus Offset (Standard 2 Ticks). **Short:** höchster Close einer grünen Kerze, plus Offset.
6. **Ziel:** R = |Einstieg − Stop|. Take-Profit = Einstieg ± 2R (nach Fill exakt auf den tatsächlichen Einstiegskurs berechnet).
7. **Sonstiges:** max. 1 Trade pro Tag · kein Einstieg nach Cutoff (Standard 10:00) · offene Position wird spätestens zum Sessionende geflattet.

### Bewusste Festlegungen (per Parameter änderbar)

| Fall | Verhalten (Default) | Parameter |
|---|---|---|
| Keine rote Kerze in den ersten 5 (bei Long-Bias) | Fallback: tiefster Close **aller** 5 Kerzen als Stop-Basis | `AllowFallbackStop` |
| Erste Pullback-Kerze schließt bereits auf/jenseits des Stops | Kein Trade an diesem Tag | `StrictFirstPullback` |
| Doji (Close == Open) | Zählt weder als rote noch als grüne Kerze | — |
| Kein Pullback bis Cutoff | Kein Trade an diesem Tag | `CutoffHour/Minute` |

### Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `OpenHour` / `OpenMinute` | 9 / 0 | Referenz-Eröffnungszeit (lokale NT-Zeitzone!) |
| `InitialBars` | 5 | Anzahl Beobachtungskerzen |
| `CutoffHour` / `CutoffMinute` | 10 / 0 | Letzte mögliche Signalkerze schließt zu dieser Zeit |
| `RewardMultiple` | 2 | Take-Profit in R |
| `StopOffsetTicks` | 2 | „leicht unter/über" der Stop-Basis (FDXS: 1 Tick = 1 Punkt = 1 €) |
| `Contracts` | 1 | Positionsgröße |
| `AllowFallbackStop` | true | s. o. |
| `StrictFirstPullback` | true | s. o. |

> **Achtung Zeitzone:** Die 9:00-Logik greift auf die in NinjaTrader eingestellte Zeitzone zu. Prüfen unter **Tools → Options → General → Time zone** → muss `(UTC+01:00) Amsterdam, Berlin, …` sein.

---

## OpeningImmediate2R — Variante 2 (Sofort-Einstieg)

Identisch zu OpeningPullback2R bis auf den Einstieg: **kein Warten auf eine Pullback-Kerze.** Der Markteinstieg erfolgt direkt nach dem Richtungsentscheid.

**Timing-Detail:** Die 5. Kerze schließt um 09:05:00 — der „sofortige" Einstieg füllt daher um **09:05:00** (Open der Folgeminute), nicht 09:06. Wer den Fill exakt um 09:06:00 will, setzt `EntryDelayBars = 1` (dann wird eine Kerze — egal welcher Farbe — abgewartet).

Stop- und Ziel-Logik unverändert: Stop an der Fensterstruktur (tiefster roter Close − Offset bzw. höchster grüner Close + Offset), Take-Profit = Einstieg ± 2R auf Basis des tatsächlichen Fills.

**Zusätzliche/entfallene Parameter gegenüber Variante 1:**

| Parameter | Default | Bedeutung |
|---|---|---|
| `EntryDelayBars` | 0 | 0 = Order beim Schluss der 5. Kerze (Fill 09:05:00) · 1 = eine Kerze später (Fill 09:06:00) |
| ~~`CutoffHour/Minute`~~ | — | entfällt (Einstieg ist deterministisch, kein Warten) |
| ~~`StrictFirstPullback`~~ | — | entfällt (kein Pullback-Konzept) |

Randfall: Schließt die 5. Kerze bereits auf/jenseits des berechneten Stops (R ≤ 0), findet kein Trade statt.

---

## Installation in NinjaTrader 8.1

**Variante A — Datei kopieren (empfohlen):**
1. `Strategies/OpeningPullback2R.cs` herunterladen.
2. Nach `Dokumente\NinjaTrader 8\bin\Custom\Strategies\` kopieren.
3. NinjaTrader starten → **New → NinjaScript Editor** → Taste **F5** (Kompilieren).
4. Unten muss „Compile successful" erscheinen.

**Variante B — Copy & Paste:**
1. **New → NinjaScript Editor** → rechts im Ordner **Strategies** Rechtsklick → **New Strategy** → Name exakt `OpeningPullback2R` → im Wizard direkt unten links auf **View Code** klicken (nichts konfigurieren).
2. Kompletten Dateiinhalt aus dem Repo einfügen (alles ersetzen) → **F5**.

---

## Daten: Das musst du VOR dem Backtest klären

NinjaTrader lädt historische Daten **automatisch vom verbundenen Datenanbieter**, sobald der Strategy Analyzer sie braucht. Es gibt aber einen Haken:

**FDXS ist ein Eurex-Produkt — die kostenlosen NinjaTrader-Daten (Free Sim Data) decken nur CME ab.** Für Eurex-Historie brauchst du einen Anbieter mit Eurex-Abo, z. B.:

- **Interactive Brokers** (TWS/Gateway laufen lassen, NT-Connection „Interactive Brokers", Eurex-Marktdaten im IB-Konto abonniert) — liefert gute 1-Min-Historie
- **IQFeed** mit Eurex-Add-on

**Wenn du heute Abend (noch) keinen Eurex-Feed hast:** Mechanik trotzdem testen — kostenlosen NinjaTrader-Account verbinden (Free Sim Data, CME) und die Strategie auf **MES 09-26** oder **MNQ 09-26** laufen lassen mit `OpenHour = 15`, `OpenMinute = 30` (US-Kassaeröffnung, Berliner Zeit). Die Logik ist identisch, nur das Instrument anders. Eurex-Daten dann in Ruhe organisieren.

**Daten prüfen/manuell laden:** **Tools → Historical Data** → links Instrument suchen → Reiter **Download** → Typ `Minute`, Zeitraum wählen → Download. Wenn dort nichts ankommt, liefert dein Anbieter für das Instrument keine Historie.

---

## Backtest im Strategy Analyzer — Schritt für Schritt

1. **Verbinden:** Control Center → **Connections** → deinen Anbieter verbinden (grünes Signal unten links).
2. **New → Strategy Analyzer**.
3. Links im Panel:
   - **Strategy:** `OpeningPullback2R`
   - **Instrument:** `FDXS 09-26` (ggf. über Instruments-Suche hinzufügen)
   - **Data series:** Type `Minute`, Value `1`
   - **Time frame:** z. B. letzte 6–12 Monate. Der SEP26-Kontrakt allein ist erst seit ~Juni 2026 liquide — für mehr Historie in den Properties **Merge policy = MergeBackAdjusted** setzen (hängt die Vorgängerkontrakte 06-26, 03-26, … kursbereinigt an).
   - **Trading hours:** Eurex-Template des Instruments (Standard belassen)
4. **Realistisch machen (wichtig!):**
   - **Commission:** Tools → Commissions → Template anlegen (FDXS grob ~1 € pro Kontrakt/Seite, je nach Broker) und im Analyzer zuweisen
   - **Slippage:** 1 Tick
   - **Order fill resolution:** `High`, darunter `1 Tick` wählen, wenn dein Anbieter Tick-Historie liefert (IQFeed ja, IB nein → dann `Standard` lassen). Bei `Standard` sind Tage, an denen Stop **und** Target in derselben 1-Min-Kerze liegen, geraten — bei 2R-Distanzen selten, aber im Trade-Log stichprobenartig prüfen.
5. **Run** klicken. Ergebnis-Tabs: **Summary** (Kennzahlen), **Trades** (jeder einzelne Trade), **Chart** (Einstiege visuell prüfen!), **Analysis**.

### Worauf schauen

- **Chart-Tab zuerst:** 5–10 Trades manuell nachvollziehen — stimmen Einstieg 9:06+, Stop-Level, 2R-Ziel? Erst wenn die Mechanik sichtbar korrekt ist, sind Kennzahlen relevant.
- Bei 2R-Zielen reicht rechnerisch eine Trefferquote > 33,3 % (vor Kosten). Relevant: **Profit Factor**, **Max Drawdown**, **Anzahl Trades** (unter ~100 Trades ist statistisch wenig belastbar — mit MergeBackAdjusted Historie verlängern).
- Danach: Parameter-Robustheit über **Optimize** + **Walk-Forward** prüfen (kleine Parameteränderung darf das Ergebnis nicht umkippen).

---

## Bekanntes Verhalten / Grenzen

- `Calculate.OnBarClose`: Der Market-Einstieg füllt zum Open der Folgeminute — das entspricht „Einstieg nach Schluss der Signalkerze".
- Feste Kontraktzahl → das €-Risiko schwankt täglich mit der Stop-Distanz (R in Punkten variiert). Positionsgrößen-Normierung auf festes €-Risiko wäre die erste sinnvolle Erweiterung.
- Läuft die Position bis Sessionende ohne Stop/Target, wird sie 30 s vor Schluss geflattet (`IsExitOnSessionCloseStrategy`).
- Mögliche Varianten für den Vergleich später: Zeit-Exit (z. B. flatten 17:30), 1R/3R-Ziele, Break-Even nach 1R.
