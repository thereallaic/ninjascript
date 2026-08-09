# NinjaTrader 8.1 Strategien — Backtest-Projekt

Repo für NinjaScript-Strategien, die im NinjaTrader 8.1 Strategy Analyzer gebacktestet werden.

## Strategien

| Datei | Name | Idee |
|---|---|---|
| `Strategies/OpeningPullback2R.cs` | OpeningPullback2R | Opening-Bias aus den ersten 5 1-Min-Kerzen vs. 9:00-Open, Einstieg auf der ersten Kerze in Bias-Richtung, Stop auf dem Close der letzten Gegenkerze, Ziel = 2R |
| `Strategies/OpeningImmediate2R.cs` | OpeningImmediate2R | Wie oben, aber SOFORTIGER Einstieg direkt nach der 5. Kerze — kein Warten auf eine Signalkerze |
| `Strategies/OpeningPullbackSwing1R.cs` | OpeningPullbackSwing1R | Wie Variante 1, aber Stop unter dem geformten Swing-Low / über dem Swing-High (Wick statt Close) und Ziel = 1R |
| `Strategies/OpeningPullbackSwingReverse1R.cs` | OpeningPullbackSwingReverse1R | Umkehrung von Variante 3: gleiches Signal, gleicher Zeitpunkt, **gedrehte Orderrichtung** |

---

## OpeningPullback2R — Regelwerk

Instrument-Ziel: **FDXS 09-26** (Micro-DAX, Eurex). 1-Minuten-Kerzen.

1. **Referenz:** Open der 09:00-Kerze.
2. **Beobachtung:** Kerzen 09:00–09:05 (5 Stück). Kein Handel in diesem Fenster.
3. **Bias:** Close der 5. Kerze **über** dem 9:00-Open → Long-Bias. **Darunter** → Short-Bias. Exakt gleich → kein Trade.
4. **Einstieg Long:** erste **grüne** Kerze nach dem Fenster → Market-Order bei deren Schluss. **Short:** erste **rote** Kerze, spiegelbildlich.
5. **Stop Long:** Close der **zuletzt gesehenen roten Kerze** vor dem Einstieg, minus Offset. **Short:** Close der zuletzt gesehenen grünen Kerze, plus Offset. Die „letzte Gegenkerze" wird ab 09:00 fortlaufend mitgeführt — sie kann also aus dem 5er-Fenster oder aus den Wartekerzen danach stammen.
6. **Ziel:** R = |Einstieg − Stop|. Take-Profit = Einstieg ± 2R (nach Fill exakt auf den tatsächlichen Einstiegskurs berechnet).
7. **Sonstiges:** max. 1 Trade pro Tag · kein Einstieg nach Cutoff (Standard 10:00) · offene Position wird spätestens zum Sessionende geflattet.

> **Fill-Timing:** Bei `Calculate.OnBarClose` geht die Market-Order beim Schluss der Signalkerze raus und wird zum **Open der Folgekerze** gefüllt. Im Chart sieht der Einstiegspfeil deshalb immer eine Kerze „zu spät" aus. Das ist korrekt und realistisch — ein Fill exakt zum Schlusskurs der Signalkerze wäre in der Praxis nicht handelbar.

### Bewusste Festlegungen (per Parameter änderbar)

| Fall | Verhalten (Default) | Parameter |
|---|---|---|
| Keine Gegenkerze seit 09:00 vorhanden | Fallback: tiefster/höchster Close **aller** 5 Fensterkerzen als Stop-Basis | `AllowFallbackStop` |
| Signalkerze schließt bereits auf/jenseits des Stops (R ≤ 0) | Nächste Signalkerze abwarten | `StrictFirstSignal` |
| Doji (Close == Open) | Zählt weder als rote noch als grüne Kerze | — |
| Keine Signalkerze bis Cutoff | Kein Trade an diesem Tag | `CutoffHour/Minute` |

### Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `OpenHour` / `OpenMinute` | 9 / 0 | Referenz-Eröffnungszeit (lokale NT-Zeitzone!) |
| `InitialBars` | 5 | Anzahl Beobachtungskerzen |
| `CutoffHour` / `CutoffMinute` | 10 / 0 | Letzte mögliche Signalkerze schließt zu dieser Zeit |
| `RewardMultiple` | 2 | Take-Profit in R |
| `StopOffsetTicks` | 2 | Puffer unter/über der Stop-Basis. **0 = exakt auf dem Close** (FDXS: 1 Tick = 1 Punkt = 1 €) |
| `UseFixedRisk` | true | Positionsgröße aus dem Geldrisiko berechnen (siehe unten) |
| `RiskPerTrade` | 100 | Betrag, den 1R kosten darf — **in Instrumentenwährung** |
| `MaxContracts` | 50 | Obergrenze der berechneten Positionsgröße |
| `Contracts` | 1 | Feste Positionsgröße — nur aktiv wenn `UseFixedRisk = false` |
| `UseWindowExtremeStop` | false | false = Stop auf dem Close der letzten Gegenkerze · true = tiefster roter / höchster grüner Close der 5 Anfangskerzen (altes Verhalten, deutlich weitere Stops) |
| `MinRiskTicks` | 0 (aus) | Signale mit einem Stop-Abstand unter diesem Wert verwerfen — siehe Warnung unten |
| `AllowFallbackStop` | true | s. o. |
| `StrictFirstSignal` | false | s. o. |

### Positionsgrößen-Normierung (1R = fester Geldbetrag)

Mit `UseFixedRisk = true` (Standard) berechnet die Strategie die Kontraktzahl selbst:

```
Kontrakte = abrunden( RiskPerTrade / (R in Punkten × Punktwert) )
```

FDXS hat **1 Punkt = 1 €**, bei `RiskPerTrade = 100` also:

| R (Punkte) | Kontrakte | Tatsächliches Risiko |
|---|---|---|
| 10 | 10 | 100 € |
| 20 | 5 | 100 € |
| 42 | 2 | 84 € |
| 60 | 1 | 60 € |
| 101 | 0 → **Trade entfällt** | — |

Warum das den Backtest überhaupt erst aussagekräftig macht: Bei fester Kontraktzahl bestimmt die zufällige Stop-Distanz, wie viel ein Trade wiegt — ein Gewinner mit 40 Punkten R zählt viermal so viel wie einer mit 10. Die Equity-Kurve misst dann Stop-Distanzen statt Signalqualität. Mit fixem Geldrisiko wiegt jeder Trade gleich, und der Profit Factor sagt tatsächlich etwas über den Edge aus.

**Drei Dinge, die du wissen musst:**

1. **Währung:** `RiskPerTrade` ist in der Währung des Instruments. FDXS notiert in **EUR** — 100 bedeutet also 100 €, nicht 100 $. Willst du wirklich 100 US-Dollar, trag beim aktuellen Kurs rund 86 ein.
2. **Abrundung:** Es gibt nur ganze Kontrakte. Bei großem R wird das tatsächliche Risiko spürbar kleiner als 100 € (siehe Tabelle). Dass FDXS nur 1 €/Punkt hat, ist hier ein Vorteil — beim großen FDAX (25 €/Punkt) wäre die Normierung praktisch unmöglich.
3. **Übergroßes R:** Ergibt die Rechnung 0 Kontrakte (R > 100 Punkte bei 100 € Risiko), wird der Trade **ausgelassen**. Das ist gewollt — die Alternative wäre, das Risikolimit zu überschreiten. Behalte im Blick, wie oft das passiert: Wenn regelmäßig Tage wegfallen, ist entweder `RiskPerTrade` zu niedrig oder die Stop-Logik zu weit.

> **⚠️ Achtung, zu enge Stops:** Der Stop auf dem Close der letzten Gegenkerze kann bei ruhigen Minuten nur wenige Punkte entfernt liegen. R wird dann winzig, das 2R-Ziel liegt in Rauschweite, und Kommission plus Slippage fressen den Trade auf — bei FDXS können 1–2 Punkte Gebühren einen 4-Punkte-R komplett neutralisieren. Nach dem ersten Backtest die Spalte **MAE/Entry-Distanz** im Trades-Tab prüfen: Wenn viele Trades ein R unter ~10 Punkten haben, `MinRiskTicks` auf 8–15 setzen und erneut laufen lassen.

> **Achtung Zeitzone:** Die 9:00-Logik greift auf die in NinjaTrader eingestellte Zeitzone zu. Prüfen unter **Tools → Options → General → Time zone** → muss `(UTC+01:00) Amsterdam, Berlin, …` sein.

---

## OpeningImmediate2R — Variante 2 (Sofort-Einstieg)

Identisch zu OpeningPullback2R bis auf den Einstieg: **kein Warten auf eine Signalkerze.** Der Markteinstieg erfolgt direkt nach dem Richtungsentscheid.

**Timing-Detail:** Die 5. Kerze schließt um 09:05:00 — der „sofortige" Einstieg füllt daher um **09:05:00** (Open der Folgeminute), nicht 09:06. Wer den Fill exakt um 09:06:00 will, setzt `EntryDelayBars = 1` (dann wird eine Kerze — egal welcher Farbe — abgewartet).

Stop- und Ziel-Logik identisch zu Variante 1: Stop = Close der zuletzt gesehenen Gegenkerze im Fenster (rot bei Long, grün bei Short) ± Offset, Take-Profit = Einstieg ± 2R auf Basis des tatsächlichen Fills. Dadurch unterscheiden sich die beiden Strategien **nur** im Einstiegszeitpunkt — genau das macht den Vergleich aussagekräftig.

**Zusätzliche/entfallene Parameter gegenüber Variante 1:**

| Parameter | Default | Bedeutung |
|---|---|---|
| `EntryDelayBars` | 0 | 0 = Order beim Schluss der 5. Kerze (Fill 09:05:00) · 1 = eine Kerze später (Fill 09:06:00) |
| `MinRiskTicks` | 0 (aus) | wie Variante 1 |
| `UseFixedRisk` / `RiskPerTrade` / `MaxContracts` | true / 100 / 50 | wie Variante 1 |
| ~~`CutoffHour/Minute`~~ | — | entfällt (Einstieg ist deterministisch, kein Warten) |
| ~~`StrictFirstSignal`~~ | — | entfällt (kein Signalkerzen-Konzept) |

Randfall: Schließt die 5. Kerze bereits auf/jenseits des berechneten Stops (R ≤ 0), findet kein Trade statt.

---

## OpeningPullbackSwing1R — Variante 3 (Swing-Stop, 1R-Ziel)

Einstiegslogik **identisch zu Variante 1** (Bias aus den 5 Anfangskerzen, Long auf der ersten grünen Kerze danach, Short auf der ersten roten). Zwei Unterschiede:

**1. Stop unter dem geformten Swing-Extrem statt auf einem Close.** Ab dem Ende des 5er-Fensters wird das tiefste Low (Long) bzw. höchste High (Short) aller Kerzen mitgeführt — **einschließlich der Signalkerze selbst**. Der Stop liegt `StopOffsetTicks` darunter bzw. darüber.

```
Long:   Stop = tiefstes Low  des Pullbacks − Offset
Short:  Stop = höchstes High des Pullbacks + Offset
```

Das ist die strukturell sauberere Platzierung: Der Stop sitzt unter dem Docht, nicht im Kerzenkörper. Ein Wick-Test des Pullback-Tiefs stoppt dich damit nicht mehr aus.

**2. Take-Profit = 1R** statt 2R (`RewardMultiple = 1`).

### Was das für das Profil bedeutet

R ist hier **systematisch größer** als in Variante 1 — der Docht liegt naturgemäß unter dem Close. Zusammen mit dem 1R-Ziel ergibt das ein völlig anderes Profil:

| | Variante 1 (Close-Stop, 2R) | Variante 3 (Swing-Stop, 1R) |
|---|---|---|
| R-Distanz | eng | weiter |
| Nötige Trefferquote (vor Kosten) | > 33 % | > 50 % |
| Stopouts durch Wick-Tests | häufig | selten |
| Kontraktzahl bei 100 € Risiko | hoch | niedriger |
| Kosten je Trade relativ zu R | hoch | niedriger |

Genau dieser Trade-off ist der interessante Vergleich: Variante 1 braucht wenige große Gewinner, Variante 3 braucht konstante Treffer. Welche gewinnt, hängt davon ab, ob der FDXS nach dem Opening wirklich durchläuft oder eher zappelt.

### Abweichende Parameter

| Parameter | Default | Bedeutung |
|---|---|---|
| `RewardMultiple` | **1** | Take-Profit in R (in Variante 1: 2) |
| `StopOffsetTicks` | 2 | Puffer unter dem Swing-Low / über dem Swing-High |
| `IncludeWindowInSwing` | false | false = nur Kerzen **nach** dem 5er-Fenster bilden den Swing · true = die 5 Anfangskerzen zählen mit (deutlich weitere Stops, wenn die 9:00-Kerze einen langen Docht hat — genau das Problem, das Variante 1 ursprünglich hatte) |
| ~~`UseWindowExtremeStop`~~ | — | entfällt, durch `IncludeWindowInSwing` ersetzt |
| ~~`AllowFallbackStop`~~ | — | entfällt (ein Low/High existiert immer, kein Fallback nötig) |

Alle übrigen Parameter (Zeiten, `UseFixedRisk`, `RiskPerTrade`, `MaxContracts`, `MinRiskTicks`, `StrictFirstSignal`) verhalten sich wie in Variante 1.

> Die Warnung zu Mini-Stops aus Variante 1 gilt hier deutlich schwächer — durch den Docht-Abstand fällt R selten unter ~5 Punkte. `MinRiskTicks` kann meist auf 0 bleiben.

---

## OpeningPullbackSwingReverse1R — Variante 4 (Umkehrung von Variante 3)

Testet die Gegenhypothese: **Ist das Signal aus Variante 3 in Wahrheit ein Kontra-Indikator?**

Alles bleibt identisch — Richtungsentscheid aus den 5 Anfangskerzen, Signalkerze, Einstiegszeitpunkt, Positionsgrößenlogik. Gedreht wird nur die Orderrichtung:

| Bias (Close 5. Kerze vs. 9:00-Open) | Auslöser | Variante 3 | Variante 4 |
|---|---|---|---|
| über dem Open | erste **grüne** Kerze | LONG | **SHORT** |
| unter dem Open | erste **rote** Kerze | SHORT | **LONG** |

Der Auslöser bleibt bewusst unverändert: Bei Long-Bias wird weiterhin auf die erste grüne Kerze gewartet — nur wird dort jetzt geshortet statt gekauft. Dadurch handeln beide Varianten **exakt dieselben Tage zur exakt selben Minute**, und der Vergleich isoliert allein die Richtung.

### Die zwei Stop-Modi

**Spiegel-Modus (Standard, `UseStructuralStop = false`)** — R wird von Variante 3 übernommen und nur um den Einstieg geklappt:

```
Long-Bias, Signalkerze schließt bei C, Swing-Low bei L, Offset O:
  Variante 3:  Stop = L − O          R = C − (L − O)     Ziel = C + 1R
  Variante 4:  Stop = C + R                              Ziel = C − 1R
```

Konkret bei C = 26280, L = 26260, O = 2: R = 22 Punkte. Variante 3 hat Stop 26258 / Ziel 26302 — Variante 4 hat Stop 26302 / Ziel 26258. **Jeder Trade ist das fotografische Negativ:** Was in Variante 3 ins Ziel lief, läuft hier in den Stop und umgekehrt. Läuft Variante 3 auf 40 % Trefferquote, muss Variante 4 auf rund 60 % kommen.

> Das gilt exakt nur bei `RewardMultiple = 1`, weil Stop und Ziel dann gleich weit entfernt sind. Stellst du auf 2R um, bleibt der Stop bei 1R stehen und die Spiegelung ist keine saubere Umkehr mehr.

**Struktureller Modus (`UseStructuralStop = true`)** — der Stop sitzt am Swing-Extrem der Gegenseite (beim Short also über dem Swing-High). Handelstechnisch die sinnvollere Platzierung, aber R und damit die Positionsgröße weichen von Variante 3 ab, es ist also kein exakter Umkehrtest mehr, sondern eine eigenständige Strategie.

### Was das Ergebnis bedeutet

- **Beide verlieren** → das Signal trägt keine Information, die Kosten fressen beide Seiten. Häufigster Fall, und ein klares Stopp-Signal für diesen Ansatz.
- **Variante 4 gewinnt deutlich** → der Opening-Pullback ist am FDXS ein Fade-Setup. Erst gegen einen zweiten Zeitraum (Out-of-Sample) prüfen, bevor du das glaubst.
- **Beide etwa bei null** → Rauschen, mehr Handelstage nötig.

Alle Parameter entsprechen Variante 3, zusätzlich nur `UseStructuralStop`.

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
