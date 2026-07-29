# System Architecture

```
                TULPAR

     Autonomous Systems Platform

                    │
     ┌──────────────┼──────────────┐
     │              │              │
   Air           Ground         Marine

                    │
              Platform Core

                    │
   ┌──────────┬──────────┬──────────┐
   │          │          │          │
Communication Mission Telemetry Plugins
                    │
               Navigation
                    │
                    AI
                    │
              Simulation
                    │
                   SDK
```

---

## Principles

- Modular
- Platform Independent
- Extensible
- Event Driven
- Simulation First