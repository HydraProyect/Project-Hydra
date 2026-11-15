## Qué cambia
- `dialogo-foco.js`: la trampa de foco de Drawer, Modal, Context Workspace, buscador global y diálogo de restablecer el menú solo mueve el foco al primer enfocable si el foco aún no está dentro del contenedor. Al abrir un diálogo con el foco fuera, el foco sigue entrando en él.
- E2E: `Ayudas.RellenarFechaAsync` rellena un CampoFecha en ISO, como hasta ahora, y comprueba en el mismo paso que el campo muestra la fecha enmascarada `dd/MM/yyyy`. Sustituye todos los `FillAsync` de fecha de la suite: 17 sitios en 12 ficheros.
- E2E: `TrazaAlFallar` graba la traza de Playwright (capturas e instantáneas del DOM) y solo la conserva si el test falla, en `tests/CaeManager.E2ETests/TestResults/trazas-playwright/`. El paso «Publicar logs de CaeManager.Web si el E2E falla» de `ci.yml` ya sube `TestResults` entero; en `ci.yml` solo cambia un comentario. Está activada en `GestorCaeCarteraMultiTenantTests.El_Gestor_CAE_recorre_su_cartera_en_dos_Tenants_beneficiarios_y_resuelve_la_cola` y en `VisitaCentroGestionadoPorCorreoE2ETests`.
- `.gitignore`: `TestResults/`, para que una traza de un fallo en local no acabe en un commit.

## Por qué
Refs #2194. No la cierra: la cerrará la traza si confirma la causa.

Desde #2185 el campo de fecha es `type="text"` con máscara, y Playwright lo rellena en dos pasos: enfocar y seleccionar, y después insertar. La trampa de foco se activa en `OnAfterRenderAsync` (`Drawer.razor:111-141`), tras un viaje de ida y vuelta al servidor, y movía el foco al primer enfocable sin mirar si ya estaba dentro (`dialogo-foco.js:20`). Si eso ocurre entre los dos pasos, el texto se pierde. Es el fallo de #2167 («Fecha de emisión» vacía en DrawerGestionDocumento).

## Evidencia
- FACT: `node --test tests/CaeManager.Web.Tests/js/dialogoFoco.test.mjs` pasa 6 de 6. Con el mutante `if (true)` (robar el foco siempre) fallan los dos casos de foco ya dentro. `DialogoFocoJsTests` (dotnet test) pasa con el original y falla con el mutante. Las mutaciones se hicieron en local sobre el fichero JS y el fichero se restauró después (git status limpio).
- FACT: `dotnet build tests/CaeManager.E2ETests -nr:false` termina con código 0. `dotnet test tests/CaeManager.Web.Tests`: 12403 correctas, 0 con error, 6 omitidas. `dotnet test tests/CaeManager.Architecture.Tests`: 1596 correctas, 0 con error (ratchet de localizadores E2E incluido). Todo sobre eb5b21e5a; el commit siguiente solo toca el `.mjs` (que se volvió a pasar) y `.gitignore`. La cabeza 00d8c1c34 es la fusión de origin/main (#2192) sin cambios propios; `dotnet format --verify-no-changes --include` sobre los .cs del diff pasa.
- FACT: `scripts\preflight-pr.ps1 -CuerpoPr` pasa sobre la cabeza.
- INFERENCE: la corrección explica #2167. No explica el fallo de #2169 («Fecha de inicio» vacía en Visitas, que arranca con la fecha de hoy): si el foco salta, el campo debería conservar la fecha de hoy, no quedar vacío. Ese caso sigue en UNKNOWN; la comprobación tras cada relleno y la traza de CI son para cazarlo.
- Hueco: los E2E corren solo en CI y no hay mutación de E2E. La comprobación nueva no se ha visto fallar: esa evidencia solo la dará un fallo real en CI.
- Hueco (BAJA, no es regresión): si la trampa se activa con el foco ya dentro del diálogo, `elementoPrevio` es un campo del propio diálogo, y al cerrarlo el foco no vuelve al botón que lo abrió. El código anterior capturaba lo mismo.
## Revisión Codex
Revisor: revisor-puente (completa, dos rondas; sin pasada de Codex: el cambio en ci.yml es solo un comentario y la PR no arma auto-merge)
SHA revisado: f9f4f3fd2bcc2cbff5b912e6e4d5e5a4dca55f67
Cobertura: 19 de 19 ficheros
Refutación: foco fuera, en body, nulo, en un campo del diálogo y en el propio contenedor, en los cinco consumidores de atraparFoco (Drawer, Modal, ContextWorkspace solo en la transición de cerrado a abierto, BuscadorGlobal, OrdenMenuLateral); los 17 campos sustituidos son CampoTexto Type="date" con máscara ISO a dd/MM/yyyy y ningún FillAsync de fecha quedó fuera; orden de liberación traza antes que contexto; filtro de excepciones de DisposeAsync que no tapa la del test; ruta de la traza contra el artefacto del job e2e-bloques; ratchets de Architecture; TestResults/ en .gitignore sin ocultar ficheros versionados ni pasos de CI; mutaciones del test de Node.
Veredicto: aceptados 2 (BAJA: carga del módulo por data: URL y TestResults/ en .gitignore, corregidos en f9f4f3fd2), 1 BAJA abierto y declarado como hueco (elementoPrevio dentro del diálogo, no es regresión); sin ALTO ni MEDIO.