# Convenciones de Código — CAE Manager

## Principios

SOLID, DRY, KISS, YAGNI. Código mantenible siempre por encima de código "rápido". Nada de abstracciones para casos hipotéticos futuros — tres líneas parecidas es preferible a una abstracción prematura.

## C# general

- `Nullable` habilitado en todos los proyectos (`<Nullable>enable</Nullable>`); un `string?` explícito comunica intención, no se suprime con `!` salvo justificación puntual comentada.
- Namespaces con archivo único (`namespace CaeManager.Domain.Clientes;`), nunca bloque con llaves.
- `record` para DTOs, Commands, Queries y Value Objects — inmutables por defecto, igualdad estructural gratis.
- `class` para entidades de dominio (tienen identidad, no igualdad estructural) y para handlers/servicios.
- Nombres completos, sin abreviaturas: `TrabajadorRepository`, no `TrabRepo`. Sin nombres genéricos (`Manager`, `Helper`, `Utils`) — si un tipo necesita ese nombre, probablemente le falta una responsabilidad clara.
- `async`/`await` de punta a punta; todo método async termina en `Async`; `CancellationToken` se propaga desde el handler de MediatR hasta la llamada a EF Core.
- Prohibido `async void` salvo handlers de eventos de UI de Blazor donde el framework lo exige.

## Dominio

- Entidades exponen comportamiento (`documento.Renovar(fechaEmision)`), no setters públicos que permitan estados inválidos (`documento.FechaEmision = x` sin pasar por invariantes).
- Invariantes se protegen en el constructor/métodos, nunca se confía en que la capa de Application las valide dos veces (aunque Application sí valida forma/formato de entrada con FluentValidation antes de llegar al dominio).
- Result pattern (`Result`, `Result<T>`) para fallos de negocio esperables; excepciones reservadas para errores de infraestructura no recuperables.

## Application (CQRS)

- Un archivo por Command/Query + su Handler + su Validator, agrupados en la misma carpeta de feature (ver `ARCHITECTURE.md`).
- **Todo Command implementa `ICommand` (sin valor de retorno) o `ICommand<T>`, nunca `IRequest<...>` a pelo.** No es cosmético: `AutorizacionEscrituraBehavior` decide por esa interfaz quién puede escribir, así que un Command sin ella lo puede ejecutar cualquier rol, incluido uno de solo lectura. El sufijo `Command` en el nombre sigue siendo obligatorio, y `ArquitecturaCommandsTests` falla en CI si nombre e interfaz no van juntos en cualquiera de las dos direcciones. Las Queries siguen usando `IRequest<T>` directamente.
- Los DTOs de salida de Queries son planos y específicos de esa query (no se reutiliza el mismo DTO "grande" para lista y detalle) — cada pantalla pide exactamente los campos que muestra.
- FluentValidation por Command/Query con reglas de formato (obligatoriedad, longitud, formato de DNI/email); las reglas de negocio (p. ej. "no se puede eliminar un Cliente con Centros activos") viven en el handler o en el dominio, no en el validator.
- Mapeo entidad→DTO: métodos de extensión estáticos explícitos (`cliente.ToDto()`) por defecto. AutoMapper solo se introduce si un mapeo concreto se vuelve genuinamente repetitivo y mecánico — nunca como configuración por defecto que oculte qué campo viene de dónde.

## Infrastructure / EF Core

- Una clase `IEntityTypeConfiguration<T>` por entidad en `Persistence/Configurations/`, nunca configuración inline en `OnModelCreating`.
- Lazy loading **desactivado**; toda relación se carga con `.Include()` explícito o, preferiblemente, se evita del todo usando proyecciones (`.Select()`) en las queries de lectura.
- Migraciones con nombre descriptivo en español-técnico (`AddCentroPlataformaAcceso`, no `Migration1`).
- Repositorios de agregado (uno por agregado raíz) solo exponen los métodos que el dominio realmente necesita (`ObtenerConCentrosActivos`, no un `GetAll()` genérico que invite a filtrar en memoria).

## Blazor (Presentation)

- Cada componente no trivial se divide en `Componente.razor` (marcado) + `Componente.razor.cs` (code-behind) — no lógica compleja embebida en `@code { }` dentro del `.razor`.
- CSS aislado por componente (`Componente.razor.css`); los tokens de `DESIGN_SYSTEM.md` se consumen como variables CSS globales, nunca valores hardcodeados en un componente individual.
- Estado de una feature (filtros activos, selección de tabla) vive en una clase `*State` inyectada como scoped service de esa feature, no en variables sueltas repartidas entre componentes.
- Los componentes de página (`Pages/`) orquestan: llaman a MediatR vía un servicio de aplicación, gestionan estado de carga/error, y delegan el render a componentes de presentación puros (`Components/`) que reciben datos por parámetro y no conocen MediatR.
- **El render mode no se propaga hacia arriba, del Body hacia el Layout.** Que una página declare `@rendermode InteractiveServer` NO hace interactivo lo que vive en `MainLayout.razor` fuera de `@Body` (menús, botones, componentes globales como `AnfitrionToasts`) — cada uno necesita su propio `@rendermode` en su punto de uso. Un Layout tampoco puede declarar `@rendermode` sobre sí mismo (falla en tiempo de ejecución: `Body` es un `RenderFragment` que no cruza el límite de serialización). Bug real encontrado al construir el buscador global: los toasts llevaban toda la Fase 2 sin funcionar por este motivo exacto, sin que ningún test lo detectara porque las aserciones comprobaban la presencia del contenedor `.toast`, no de un toast real. Si un componente vive en el Layout y necesita interactividad, dale su propio `@rendermode InteractiveServer` en el punto donde el Layout lo usa.

## Testing

- Toda regla de negocio no trivial (empezando por el cálculo de estado de Documento, ver `DATABASE.md`) tiene pruebas unitarias en `CaeManager.Domain.Tests` antes de considerarse terminada.
- Handlers de Application se prueban en `CaeManager.Application.Tests` con repositorios fake/mock — sin base de datos real.
- Flujos completos (migraciones + queries reales) se prueban en `CaeManager.IntegrationTests` contra SQLite en archivo temporal, no en memoria (el proveedor in-memory de EF Core no valida constraints reales).
- Framework: xUnit + FluentAssertions.

## Git

- Commits en español o inglés consistente por sesión, en modo imperativo ("Agrega validación de DNI", no "Agregado" ni "Agregando").
- Un commit = un cambio coherente; no mezclar refactor con feature nueva en el mismo commit salvo que sean inseparables.
- Ramas por feature siguiendo el patrón ya asignado al proyecto (`claude/...`); nunca commitear directo si existe flujo de PR establecido.

## Checklist de revisión (aplicable a cualquier PR)

- [ ] ¿Respeta la organización Feature-First dentro de su capa?
- [ ] ¿El dominio protege sus invariantes o confía en que "alguien más" valide?
- [ ] ¿Hay estados Loading/Empty/Error/Forbidden contemplados si es UI?
- [ ] ¿El microcopy está en español y sigue el tono de `UX_PATTERNS.md`?
- [ ] ¿Hay una prueba que falle si la regla de negocio se rompe?
- [ ] ¿Se introdujo una abstracción que no tiene todavía un segundo caso de uso real? Si sí, simplificar.
- [ ] **¿Hay al menos un test E2E (`tests/CaeManager.E2ETests`) o bUnit (`tests/CaeManager.Web.Tests`) del flujo nuevo que añade esta fase**, si toca un flujo de usuario o un componente de lógica no trivial? Esta regla existe precisamente porque las Fases 7-23 se cerraron sin ella y generaron un backlog de 13 bugs/mejoras sin cubrir — ver ROADMAP.md, "Iniciativa de hardening" § 2 y § 8.
- [ ] ¿CI está en verde (los 6 checks de `ci.yml`) antes de mergear, no solo "compila en mi máquina"?
- [ ] ¿Se actualizó `ROADMAP.md` con lo hecho/pendiente de esta fase, siguiendo el formato ya establecido (✅/🟡/⬜ + fecha)?
