# DroneFollow – Verhaltens-State-Machine

`BehaviourMachine` (`Assets/Scripts/Behaviour/`) ist die einzige Instanz, die entscheidet, was die Drohne tut. Die Policy (PPO)
liefert nur einen Vorschlag und wird ausschließlich im Zustand **Follow** durchgereicht. Die Logik ist reines C# (Zeit kommt als `dt`),
läuft identisch im Simulator und auf der echten AR.Drone und ist per EditMode-Tests abgedeckt.

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Takeoff: Operator TAKEOFF (Link ok, Drohne gelandet)
    Takeoff --> Hover: Hovering/Flying
    Takeoff --> Failsafe: Timeout 8 s
    Hover --> Search: SEARCH, oder FOLLOW ohne Ziel
    Hover --> Follow: FOLLOW mit Ziel
    Search --> Follow: Ziel >= 0.5 s stabil
    Search --> Hover: Timeout 45 s (Operator-Suche)
    Search --> Landing: Timeout 45 s (automatische Suche)
    Follow --> LostTarget: Ziel > 1.5 s weg
    LostTarget --> Follow: Ziel wieder da
    LostTarget --> Search: > 3 s ohne Ziel
    LostTarget --> Landing: > 20 s ohne Ziel
    Follow --> Hover: Operator HOVER
    Landing --> Idle: gelandet
    Failsafe --> Idle: gelandet
```

Aus jedem Flugzustand: Operator **LAND** → Landing. **Failsafe** bei Link-Verlust > 1 s, echter App-Pause (nicht der REC-Dialog),
Akku (siehe unten) oder Takeoff-Timeout. Drohnen-`Emergency` → Idle (Motoren aus).

## Parameter (`BehaviourSettings`)
| Name | Start | Bedeutung |
|---|---|---|
| followLostSeconds | 1.5 | Follow → LostTarget |
| lostToSearchSeconds / lostToLandSeconds | 3 / 20 | Gesamtzeit ohne Ziel |
| acquireSeconds | 0.5 | Ziel muss so lange durchgehend sichtbar sein |
| searchYaw | 0.35 | Drehrate in Search (≈ 35 °/s, eine Runde ≈ 10 s) |
| searchClimb / searchClimbSeconds | 0.3 / 1.4 | Steigschritt (~0.3 m) nach jeder Runde |
| searchMaxAltitude / maxAltitude | 2.0 / 2.5 m | Höhenlimits |
| searchTimeoutSeconds | 45 | Suchzeitlimit |
| landBelowBatteryPercent | 12 | Akku-Grenze |
| batteryWindowSeconds / batteryDebounceSeconds | 5 / 2 | siehe unten |

## Akku
Der AR.Drone-Prozentwert bricht unter Last stark ein (100 → 58 % im Flug, danach wieder 86 %). Verwendet wird deshalb das **Maximum der
letzten 5 s**; erst wenn auch dieses 2 s lang unter der Grenze liegt, landet die Drohne. Ein kurzer Spannungseinbruch löst nichts aus.

## Shadow-Modus
Die Machine läuft komplett, der Adapter (`DroneBehaviour`) sendet aber keine Move-Befehle (Hover). Takeoff/Land bleiben möglich.

## Zwilling
`DroneFollowInference.unity` führt Policy + Machine auf dem `SimulatedDroneController` aus, mit echtem YOLO/OSNet/MiDaS auf dem gerenderten
Bild. Grenzen: kein Akku-Sag, keine Funkprobleme, kein Optical-Flow-Drift (nur über Test-Hooks einspeisbar).

## Tests
`Assets/Editor/Tests/BehaviourMachineTests.cs` (EditMode): `Unity -batchmode -runTests -testPlatform EditMode -projectPath .`
