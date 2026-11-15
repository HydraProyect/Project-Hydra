## Qué cambia

Un Tenant propietario solo puede tener una Empresa propia activa (`Empresa.EsPropia = true`), sea cual sea su Producto Contratado: P, O o ninguno (Tenant TEC participante). La guarda vive en Application (`ILimiteEmpresaPropia`, `src/CaeManager.Application/Empresas/LimiteEmpresaPropia.cs`) y la aplican todos los escritores que pueden dejar una Empresa propia activa más. Los Clientes empresariales y las Subcontratas (`EsPropia = false`) no cuentan. Cada rechazo avisa a Soporte TALVEG por `IAlertaOperativa`, una vez por comando, con el Tenant y sin razón social ni CIF.

El recuento que decide y el guardado van bajo `ILimiteEmpresaPropia.SerializarAsync`: un `pg_advisory_xact_lock(hashtextextended('app.limite_empresa_propia:' || TenantId, 0))` en la misma transacción que el `SaveChanges` (`src/CaeManager.Infrastructure/Persistence/CandadoEmpresaPropia.cs`), que dura hasta el COMMIT. Quien toma también el candado de altas con acreditación lo toma antes que este.

## Por qué

Decisiones de producto D-P11 (2026-10-30), ajuste del 2026-11-12 (todo Tenant, P, O y Tenant TEC participante, tiene una sola Empresa propia salvo ampliación de grupo aprobada; para O ya no hay excepción) y EP-1 (2026-11-12: el Tenant TEC gratuito tiene una sola Empresa propia).

## Escritores de una Empresa propia

`EsPropia` tiene setter privado; el único camino que la deja a `true` es el constructor público de `Empresa` (las factorías `CrearComoCliente` y `CrearComoSubcontrata` la ponen a `false`), más las restauraciones desde la Papelera.

| Escritor | Guarda | Aviso a Soporte TALVEG |
|---|---|---|
| `CrearEmpresaCommand` (Drawer «Nueva empresa», alta guiada paso 1, alta rápida) | recuento, `Agregar` y guardado dentro del candado | sí |
| `EjecutarImportacionCommand` (CAE completa) | por fila, contando las propias ya añadidas sin guardar; bajo el candado, primero la marca de la operación y después el recuento final | uno por comando |
| `EjecutarImportacionCombinadaCommand` | igual que la simple | uno por comando |
| `EjecutarImportacionMaestraCommand` | todo el confirmar bajo el candado (después del de altas); la marca se relee antes de procesar; la vista previa omite sin avisar | uno por comando, solo al confirmar |
| `RestaurarEmpresaCommand` | si la fila es propia, recuento, `Restaurar()` y guardado dentro del candado | sí |
| `RestaurarClienteCommand` | igual, si la fila es propia (estado heredado) | sí |
| `RestaurarSubcontrataCommand` | igual, si la fila es propia (estado heredado) | sí |
| Seeders (`DatosPruebaSeeder`, `EscenariosDireccionDemoSeeder`, `CoberturaPantallasSeeder`, `GestorCaeCarteraMultiTenantSeeder`, `PilotoOutboundSeeder`) | no pasan por la guarda | no |

Alta autónoma de Tenant, onboarding, solicitud de alta y registro no crean Empresa (revisión puente, ronda 1). No hay endpoint HTTP que cree Empresas.

La prueba de arquitectura `AltaDeEmpresaPropiaSoloTrasElLimiteTests` (Roslyn, por método) ve `new Empresa(...)` con nombre, alias o tipo destino, `Restaurar()` sobre una Empresa, `SetProperty` y SQL sobre `EsPropia`/`EstaEliminado`. Exige que cada método escritor compruebe el límite y que la comprobación que decide y el guardado vayan dentro del delegado de `SerializarAsync` sobre un receptor declarado como `ILimiteEmpresaPropia` (o, en las importaciones, que el método que prepara fila a fila no guarde fuera del candado salvo en la rama sin Empresas propias nuevas). Tiene casos positivos y negativos del propio instrumento.

## E2E ajustados

Los E2E de la colección compartida entran como Administrador en el Tenant #1 y daban de alta una Empresa propia en cada clase. `WebAppFixture.LiberarEmpresaPropiaDelTenantPrincipalAsync` convierte por SQL las Empresas propias activas de ese Tenant en Subcontratas coherentes (`EsPropia = false` y `NivelServicio = Gestionada`, la forma de `Empresa.CrearComoSubcontrata`), solo de ese Tenant y sin pasar por la guarda, justo antes de cada alta: FlujoCritico, FlujoBandejaPriorizada, ImportarDocumentos, SubidaMasiva, AltaGuiada, AtajosFicha360, CentrosGestionarEnVivo, FlujoCicloDocumental, VisitaCentroGestionadoPorCorreo, Importacion e ImportarCombinado.

En VinculacionUsuarioClienteRelacionEmpresarial, el control negativo vuelve a comprobar que el Cliente empresarial no ve en `/empresas` del portal una Empresa propia ajena, además de la Subcontrata ajena en `/subcontratas`; la Subcontrata ajena se da de alta antes que la Empresa propia, con precondición SQL de que no queda vinculada a ella. Los localizadores congelados de ese E2E suben de 3 a 4 (`FiltroPorTexto`) y de 2 a 3 (`Posicional`) por ese control restaurado: la espera del estado vacío del portal (`.estado-vacio, .aviso-sin-asignacion-cartera` con `.First`, porque la página muestra uno u otro) y la aserción de que no aparece la fila de la Empresa propia ajena (`.tarjeta-fila-acordeon` con `HasText`), el mismo patrón que la comprobación de `/subcontratas` del mismo test. Sin ellos se perdía el control negativo que pidió Codex r1.

Los tests de integración que construyen los handlers a mano reciben el doble `LimiteEmpresaPropiaSinAplicar`, salvo `CandadoEmpresaPropiaTests`, que usa el límite y el candado reales.

## Evidencia

- FACT: `scripts/preflight-pr.ps1 -CuerpoPr` PASA sobre 86720e4a58712f7d0db159a1749481a8c6d018b6, la cabeza de la PR (1638d1256 más el merge de origin/main con #2192, sin conflictos) (compilación de la solución con warnings como errores, formato, Domain 2350, Application 7564, Architecture 1635, Web 12408, gobernanza del cuerpo).
- FACT: sobre 779831d94, `dotnet test` de Application en verde (7564 de 7564), con los dos tests nuevos de la segunda confirmación de la misma operación (simple y combinada): la primera confirmación se simula al tomar el candado y la segunda da «ya ejecutada» sin error de límite ni alerta.
- FACT: `dotnet test` de Architecture con el filtro `AltaDeEmpresaPropiaSoloTrasElLimite` en verde sobre 1638d1256 (39 tests), con los siete escritores reales aceptados y los casos negativos rechazados: comprobación antes del candado y guardado después, comprobación dentro y guardado después, guardado dentro y otro fuera, guardado fuera en una función local, `SerializarAsync` y `ComprobarAltaAsync` homónimos de otro tipo, rama de cero de otro recuento o del recuento de otro método, y escritura inmediata (`ExecuteUpdateAsync`) antes del candado.
- FACT: `tests/CaeManager.IntegrationTests` compila sin warnings. INFERENCE: sus pruebas nuevas solo corren en CI y no se han ejecutado sobre esta rama.
- INFERENCE: los dos tests unitarios de idempotencia fallarían con el orden anterior (el recuento final vería la Empresa propia que añade la primera confirmación y devolvería el error de límite); no se ha medido con una mutación.
- E2E e integración: solo corren en CI.

## Huecos declarados

- Seeders: `DatosPruebaSeeder.cs` y `EscenariosDireccionDemoSeeder.cs` crean varias Empresas propias en un mismo Tenant (en la demo, 24 en un Tenant) y quedan exentos en la prueba de arquitectura; Codex r1 lo marcó ALTO porque `DatosPrueba:Activo` puede ejecutarse también en producción. Cambiarlos afecta a la siembra de la demo y de staging, y lo decide Chris. Los Tenants que ya tengan más de una no se tocan: solo se impide crear.
- Mutación del candado: no medida. Ni quitar el candado ni sacar el guardado de `SerializarAsync` se han empujado como mutación a una rama desechable; la sensibilidad se apoya en los casos negativos del instrumento, en el control del observador de integración y en razonamiento.
- `CandadoEmpresaPropiaTests` prueba en PostgreSQL que guardan con el candado tomado el alta, `RestaurarEmpresa` y la importación simple, y la carrera de dos confirmaciones solo para la simple. Para `RestaurarCliente`, `RestaurarSubcontrata` y las importaciones combinada y maestra, el guardián es el instrumento sintáctico y los tests unitarios con el candado en memoria.
- Límites del instrumento: receptores, métodos, guardados y el recuento de la rama de cero se reconocen por nombre; no ve un guardado con otro nombre o a través de otro tipo, ni el orden entre comprobación y guardado, ni si se usa el resultado de la comprobación, ni otros llamantes sin candado de un método alcanzado.
- No existe la ampliación de grupo: el rechazo es total, también para Soporte TALVEG en Sesión Privilegiada. El mensaje al usuario no la promete.
- La Papelera no cuenta: eliminar la única Empresa propia y dar de alta otra cambia de Empresa propia sin Soporte TALVEG. Proteger la Empresa propia frente a eliminar es una decisión de producto aparte.
- `Tenant.EsPlataforma`: el límite no distingue el Tenant de plataforma TALVEG (el Tenant #1 de los E2E); falta decidir si le aplica.
- Ningún E2E ejerce el rechazo: los E2E liberan la Empresa propia antes de cada alta.
- Alta guiada: el camino por defecto del paso 1, «Empresa nueva», no tiene salida para un Tenant que ya tiene su Empresa propia (pregunta de producto).
- BAJO sin corregir: el aviso no tiene freno ni deduplicación por Tenant; una consulta `COUNT` por fila nueva en las importaciones; la vista previa de las importaciones simple y combinada no refleja el límite y los Trabajadores dependientes de la fila omitida reciben un motivo genérico; la Empresa liberada se rotula como Subcontrata en la Bandeja de los E2E sin aserción que la lea.

## Revisión Codex
Revisor: Codex (dos rondas) y revisor-puente (completa; tres rondas antes de Codex y dos rondas después)
SHA revisado: 7e41a91c32bfda3788cec802b9f06bcf841c8c14 (revisor-puente r5); Codex r2 sobre 0114f486558a0e27139d836e6e74565f1023ad77. Después, 1638d1256 solo añade casos y comentario del instrumento, y 86720e4a5 es el merge de origin/main (#2192), sin revisar.
Cobertura: Codex r2 19 de 19 ficheros del delta sobre 0114f4865; revisor-puente r4 6 de 6 ficheros del delta 0114f4865..779831d94 y r5 1 de 1 del delta 779831d94..7e41a91c3
Refutación: carrera de dos altas, de un alta y una restauración y de dos confirmaciones de la misma importación; orden de la marca de operación frente al límite bajo el candado; guardado fuera del candado (directo, por función local, por escritura inmediata); receptores y comprobaciones homónimos de otro tipo; falsos verdes del observador de pg_locks; escritores de EsPropia fuera de seeders; recuento con IgnoreQueryFilters, Sesión Privilegiada y otro Tenant; filas sin guardar en importaciones; alcance del SQL de los E2E.
Veredicto: Codex r1 sobre 3fe8e837b, NO FUSIONABLE (2 ALTO, 3 MEDIO): corregidos en 0114f4865 la carrera (ALTO 1, candado por Tenant en los siete escritores), el instrumento (MEDIO, de regex a Roslyn por método con casos negativos), el helper E2E (MEDIO, Subcontrata coherente) y el control negativo de Vinculación (MEDIO, vuelve `/empresas`); declarado el seeder (ALTO 2, lo decide Chris). Codex r2 sobre 0114f4865, NO FUSIONABLE (3 MEDIO): corregidos en 779831d94 la idempotencia (la marca de la operación antes que el límite en las importaciones simple y combinada; la maestra ya tenía ese orden), el instrumento (comprobación y guardado dentro del delegado de `SerializarAsync` sobre un receptor `ILimiteEmpresaPropia`, casos negativos) y la integración (observador de SaveChanges contra pg_locks para alta, restauración e importación, control del observador y carrera de dos confirmaciones). Revisión puente r4 sobre 779831d94, 4 hallazgos (1 MEDIO, 3 BAJO): corregidos en 7e41a91c3 la escritura inmediata fuera del candado (MEDIO), el guardado sin receptor (BAJO), el comentario que sobrestimaba la integración (BAJO) y la rama de cero por nombre a nivel de tipo (BAJO, ahora por método). Revisión puente r5 sobre 7e41a91c3, 3 hallazgos BAJO: corregidos en 1638d1256 (casos negativos de ExecuteDelete, ExecuteSql, guardado sin receptor y con acceso condicional; el acceso condicional cuenta como guardado; el comentario dice que los tests unitarios con el candado en memoria prueban el recuento dentro, no dónde se guarda). El delta 7e41a91c3..1638d1256 (solo tests y comentario del instrumento) no ha tenido revisión propia.
Hallazgos de Codex r1 y r2 y de la revisión puente r4 y r5: aceptados 14, rechazados 0, y 1 declarado como hueco (el seeder, ALTO 2 de Codex r1).

## Fusión

Pendiente de Codex (superficie sensible). Sin auto-merge: la fusión la decide Chris.

