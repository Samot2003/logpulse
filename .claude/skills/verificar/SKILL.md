---
name: verificar
description: Verifica un hito de LogPulse lanzando en paralelo los agentes verificadores que exige docs/ROADMAP.md y consolida sus veredictos. Úsalo con "/verificar N" antes de cerrar un hito, o cuando el usuario pida verificar, revisar o validar el estado del proyecto.
argument-hint: "[número de hito]"
---

# /verificar

Verifica el hito indicado en `$ARGUMENTS` (por ejemplo `/verificar 4`). Sin argumento, verifica el primer hito sin marcar (`## [ ] H…`) de `docs/ROADMAP.md`.

## Pasos
1. **Lee `docs/ROADMAP.md`** y localiza la sección del hito: su línea `**Agentes:**`, sus casillas y su criterio de aceptación. Si el hito no existe o no tiene agentes, dilo y para.
2. **Determina el alcance** para los revisores: los commits del hito (`git log --oneline`) más los cambios sin confirmar (`git status --short`). Si hay cambios sin confirmar, avisa de que también se revisan.
3. **Lanza en paralelo, en un solo mensaje, una llamada al tool Agent por cada agente de la lista**, con `subagent_type` igual al nombre del agente (`qa-tester`, `code-reviewer`, `security-auditor`, `devops-verifier`, `api-tester`, `ui-tester`, `portfolio-reviewer`) y `run_in_background: false`. En el prompt de cada uno incluye:
   - Hito y objetivo (copia la sección del roadmap).
   - Alcance: rango de commits y archivos relevantes.
   - Recordatorio: solo verificar e informar con su formato de respuesta, sin modificar archivos.
4. **Consolida** los resultados en una tabla:

   | Agente | Veredicto | Bloqueantes | Avisos |
   |---|---|---|---|

   Debajo, lista los bloqueantes primero (con `archivo:línea` y evidencia) y después los avisos. Elimina duplicados entre agentes y quédate con la explicación más concreta.
5. **Comprueba las casillas** del hito contra lo que han verificado los agentes y el estado real del repo. Indica cuáles se cumplen y cuáles no.
6. **Decide:**
   - Si hay algún `FALLA` o bloqueante: el hito **no** está terminado. Propón el orden de corrección. No marques nada en el roadmap.
   - Si todo es `OK` o `AVISOS`: el hito puede cerrarse. Marca `[x]` en las casillas cumplidas y en el título del hito en `docs/ROADMAP.md`, y enumera los avisos que quedan pendientes.

## Reglas
- Esta skill **no corrige código**. Las correcciones las hace el agente principal después, en otro paso, y luego se vuelve a lanzar `/verificar`.
- Nunca inventes resultados de un agente: si uno falla o no termina, repórtalo como "sin veredicto" y trátalo como `FALLA`.
- Responde en español.
