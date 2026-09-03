using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Importacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Importacion.Commands.EjecutarImportacion;

/// <summary>
/// Escribe en la base de datos un plan ya analizado por
/// AnalizarImportacionExcelQuery — el plan viaja completo en el propio
/// comando porque Blazor Server ya lo tiene en memoria en el circuito del
/// usuario (se generó al analizar, un paso antes) y releer el Excel dos
/// veces sería trabajo duplicado. Reutiliza entidades ya existentes por
/// nombre/DNI en vez de duplicarlas — importar el mismo archivo dos veces
/// no crea filas repetidas.
/// </summary>
public record EjecutarImportacionCommand(PlanImportacionDto Plan) : ICommand<ResultadoImportacionDto>;

public record ResultadoImportacionDto(
    int ClientesCreados,
    int CentrosCreados,
    int EmpresasCreadas,
    int TrabajadoresCreados,
    int DocumentosCreados,
    int AsignacionesCreadas,
    IReadOnlyList<ItemImportacionDto> Advertencias,
    IReadOnlyList<ItemImportacionDto> Omitidos);

/// <summary>
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis,
/// ratificada por DCR-12 decisión B, propietario 2026-08-24): la importación
/// admite éxito parcial con errores reportados, pero ninguna fila de un flujo
/// soportado —Centros_Plataformas incluido— puede desaparecer sin quedar
/// registrada en <see cref="ResultadoImportacionDto.Omitidos"/> con hoja,
/// fila, descripción y motivo.
///
/// La misma decisión instruye explícitamente NO uniformizar las ramas de
/// descarte: el contrato es uniforme, la implementación no. Cada rama nombra
/// su causal concreta —qué dependencia faltó y por qué— en vez de un motivo
/// genérico, y la de Asignación→Centro distingue el Centro que el archivo ni
/// siquiera declaraba del que venía en Centros_Plataformas pero no pudo
/// crearse en esta misma importación.
///
/// La deduplicación es la excepción razonada, no un olvido: reutilizar una
/// entidad que ya existe no es descartar una fila. El paso de análisis ya lo
/// anuncia marcando la fila <c>YaExiste</c>, y la vista previa no la cuenta
/// como «Crear …» (Importacion.razor.cs cuenta solo <c>!YaExiste</c>). Solo
/// se registra el caso en que el plan SÍ prometió crearla y el estado cambió
/// entre analizar y confirmar — si no, la pantalla final prometería «1 nueva»
/// y confirmaría «0» sin explicación.
/// </summary>
public class EjecutarImportacionCommandHandler(
    IEmpresaRepository empresaRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    IDocumentoRepository documentoRepositorio,
    IAsignacionRepository asignacionRepositorio,
    IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EjecutarImportacionCommand, Result<ResultadoImportacionDto>>
{
    public async Task<Result<ResultadoImportacionDto>> Handle(EjecutarImportacionCommand request, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        // F3c (2026-08-28): leía la tabla legacy Clientes, congelada desde
        // F3b — un Cliente creado tras el freeze no se reconocía al importar y
        // su fila se omitía con el motivo "no existe todavía". "Cliente" es la
        // Empresa contraparte con EsCritico != null, mismo discriminador que
        // ObtenerClientesQuery.
        var clientesPorNombre = await empresasContext.Empresas.Where(e => e.EsCritico != null).ToDictionaryAsync(
            c => c.RazonSocial, c => c.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var centrosPorNombre = await centrosContext.Centros.ToDictionaryAsync(
            c => c.Nombre, c => c.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var empresasPorRazonSocial = await empresasContext.Empresas.ToDictionaryAsync(
            e => e.RazonSocial, e => e.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        // Un trabajador anonimizado (Dni null) no puede ser destino de un
        // emparejamiento por DNI: queda fuera del diccionario.
        var trabajadoresPorDni = await trabajadoresContext.Trabajadores
            .Where(t => t.Dni != null)
            .ToDictionaryAsync(t => t.Dni!, t => t.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var tiposDocumentoPorNombre = await tiposDocumentoContext.TiposDocumento.ToDictionaryAsync(
            t => t.Nombre, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var clientesCreados = 0;
        var centrosCreados = 0;
        var empresasCreadas = 0;
        var trabajadoresCreados = 0;
        var documentosCreados = 0;
        var asignacionesCreadas = 0;

        // Cada intento de construcción va protegido: una fila del Excel con un
        // dato inesperado (un nombre demasiado largo, un formato que el parser
        // no detectó) no debe abortar el resto de una importación por lo demás
        // válida — se reporta como omitida igual que los casos ya detectados
        // al analizar.
        var omitidosEnEscritura = new List<ItemImportacionDto>();

        // Centros que este archivo declaraba en Centros_Plataformas, con lo que el
        // análisis sabía de cada uno. Sirve para que una Asignación huérfana
        // distinga sus tres causales: el centro que el archivo ni menciona, el que
        // venía en el archivo y no pudo crearse, y el que sí existía al analizar y
        // ha dejado de existir antes de confirmar.
        var centrosDeclaradosEnElArchivo = plan.ClientesCentros
            .GroupBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().YaExisteCentro, StringComparer.OrdinalIgnoreCase);

        foreach (var fila in plan.ClientesCentros)
        {
            // Cliente ahora exige CIF y Centro exige Empresa (Fase 10) — ninguno de
            // los dos formatos de Excel soportados hoy recoge esos datos todavía,
            // así que aquí solo se reutilizan Cliente/Centro que ya existían antes
            // de importar. Crear cualquiera de los dos de cero se deja para cuando
            // la plantilla incluya esos campos; mientras tanto se omite con un
            // motivo explícito en vez de fallar o inventar un CIF/Empresa falsos.
            if (!clientesPorNombre.ContainsKey(fila.Nombre))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Centros_Plataformas", 0, fila.Nombre,
                    "Este cliente no existe todavía. Ahora requiere un CIF, que esta plantilla no recoge — créalo manualmente en Clientes."));
                continue;
            }

            var clienteId = clientesPorNombre[fila.Nombre];

            if (!centrosPorNombre.ContainsKey(fila.Nombre))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Centros_Plataformas", 0, fila.Nombre,
                    "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros."));
            }
        }

        foreach (var fila in plan.Empresas)
        {
            if (empresasPorRazonSocial.ContainsKey(fila.RazonSocial))
            {
                // Reutilización, no descarte: el análisis ya la marcó YaExiste y la
                // vista previa no prometió crearla. Solo se registra si el plan sí
                // la prometía y apareció entre analizar y confirmar.
                if (!fila.YaExiste)
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        "Empleados", 0, fila.RazonSocial,
                        $"La empresa «{fila.RazonSocial}» ya existía al confirmar la importación, aunque no existía al analizar el archivo; se reutiliza la que ya había en vez de duplicarla."));
                continue;
            }

            try
            {
                var empresa = new Empresa(fila.RazonSocial);
                empresaRepositorio.Agregar(empresa);
                empresasPorRazonSocial[fila.RazonSocial] = empresa.Id;
                empresasCreadas++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto("Empleados", 0, fila.RazonSocial, ex.Message));
            }
        }

        foreach (var fila in plan.Trabajadores)
        {
            if (trabajadoresPorDni.ContainsKey(fila.Dni))
            {
                // Reutilización anunciada por el análisis (ver nota de la clase).
                if (!fila.YaExiste)
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        "Empleados", 0, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                        $"Ya existía un trabajador con el DNI {fila.Dni} al confirmar la importación, aunque no existía al analizar el archivo; se reutiliza el que ya había en vez de duplicarlo."));
                continue;
            }

            if (!empresasPorRazonSocial.TryGetValue(fila.RazonSocialEmpresa, out var empresaId))
            {
                // Dependencia dentro del propio archivo: la Empresa del trabajador
                // ni existía antes ni pudo crearse en esta importación.
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Empleados", 0, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})",
                    $"La empresa «{fila.RazonSocialEmpresa}» no existe y no pudo crearse al importar este archivo; un trabajador no puede darse de alta sin la empresa a la que pertenece."));
                continue;
            }

            try
            {
                var trabajador = Trabajador.DeEmpresa(empresaId, fila.Nombre, fila.Apellidos, fila.Dni, fila.FechaNacimiento, fila.Email);
                trabajadorRepositorio.Agregar(trabajador);
                trabajadoresPorDni[fila.Dni] = trabajador.Id;
                trabajadoresCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto("Empleados", 0, $"{fila.Nombre} {fila.Apellidos} ({fila.Dni})", ex.Message));
            }
        }

        var documentosExistentes = await documentosContext.Documentos
            .Select(d => new { d.TrabajadorId, d.TipoDocumentoId })
            .ToListAsync(cancellationToken);
        var clavesDocumentosExistentes = documentosExistentes
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        foreach (var fila in plan.Documentos)
        {
            if (!trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorId))
            {
                // El documento se queda sin titular: su trabajador no existía ni
                // pudo crearse (su propia fila se omitió más arriba, con su motivo).
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Empleados", 0, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                    $"El trabajador con DNI {fila.Dni} no existe y no pudo crearse al importar este archivo; su documento no tiene a quién asociarse."));
                continue;
            }

            if (!tiposDocumentoPorNombre.TryGetValue(fila.NombreTipoDocumento, out var tipoDocumento))
            {
                // Referencia a una entidad del catálogo que no existe: a diferencia
                // del caso anterior, este archivo nunca pudo crearla — el catálogo
                // de tipos de documento no se alimenta desde la importación.
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Empleados", 0, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                    $"El tipo de documento «{fila.NombreTipoDocumento}» no existe en el catálogo del sistema y la importación no lo crea; da de alta el tipo en Tipos de documento y vuelve a importar."));
                continue;
            }

            if (!clavesDocumentosExistentes.Add((trabajadorId, tipoDocumento.Id)))
            {
                // Reutilización anunciada por el análisis (ver nota de la clase).
                if (!fila.YaExiste)
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        "Empleados", 0, $"{fila.Dni} — {fila.NombreTipoDocumento}",
                        $"El trabajador {fila.Dni} ya tenía un documento de tipo «{fila.NombreTipoDocumento}» al confirmar la importación, aunque no lo tenía al analizar el archivo; se conserva el que ya había en vez de duplicarlo."));
                continue;
            }

            try
            {
                var fechaVencimiento = CalculadoraEstadoDocumento.CalcularFechaVencimiento(fila.FechaEmision, tipoDocumento.VigenciaMeses);
                var documento = Documento.DeTrabajador(trabajadorId, tipoDocumento.Id, fila.FechaEmision, fechaVencimiento);
                documentoRepositorio.Agregar(documento);
                documentosCreados++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto("Empleados", 0, $"{fila.Dni} — {fila.NombreTipoDocumento}", ex.Message));
            }
        }

        // Trae TODAS las asignaciones existentes, activas o cerradas: DEC-19
        // exige comprobar solape de rango también contra las ya cerradas, no
        // solo si el par sigue activo — el hueco que
        // IX_Asignaciones_TenantId_TrabajadorId_CentroId_Activa nunca cubrió.
        var asignacionesExistentes = await asignacionesContext.Asignaciones
            .Select(a => new { a.TrabajadorId, a.CentroId, a.FechaBaja })
            .ToListAsync(cancellationToken);
        var clavesAsignacionesActivas = asignacionesExistentes
            .Where(a => a.FechaBaja is null)
            .Select(a => (a.TrabajadorId, a.CentroId))
            .ToHashSet();
        // Mismo límite exclusivo que Asignacion.SeSolapaCon: la asignación
        // nueva es un rango abierto [hoy, ∞), así que solapa con una fila ya
        // cerrada exactamente cuando esa fila se cerró después de hoy.
        var clavesAsignacionesSolapanConCerrada = asignacionesExistentes
            .Where(a => a.FechaBaja is not null && hoy < a.FechaBaja.Value)
            .Select(a => (a.TrabajadorId, a.CentroId))
            .ToHashSet();

        // A diferencia de las demás hojas, el análisis NO deduplica pares
        // (trabajador, centro) repetidos dentro del propio archivo: la hoja
        // Asignaciones se recorre por filas y dos filas con el mismo nombre
        // completo producen el mismo par. Sin esta pista, la segunda se
        // confundiría con una asignación aparecida entre analizar y confirmar y
        // se le daría un motivo falso — dos causales distintas, un solo mensaje.
        var clavesAsignacionesDelPlan = new HashSet<(Guid, Guid)>();

        foreach (var fila in plan.Asignaciones)
        {
            if (!trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorId))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Asignaciones", 0, $"{fila.Dni} — {fila.NombreCentro}",
                    $"El trabajador con DNI {fila.Dni} no existe y no pudo crearse al importar este archivo; sin él la asignación al centro «{fila.NombreCentro}» no puede crearse."));
                continue;
            }

            if (!centrosPorNombre.TryGetValue(fila.NombreCentro, out var centroId))
            {
                // DCR-12 B exige distinguir aquí la causal, no dar un motivo común.
                // Son tres situaciones distintas y el usuario actúa distinto en cada
                // una: el Centro que este archivo declaraba y no pudo crearse (Fase 10
                // le exige una Empresa que la plantilla no recoge), el que el archivo
                // ni siquiera menciona, y el que sí existía al analizar y ha dejado de
                // existir antes de confirmar — a ese último no se le puede decir que
                // "no pudo crearse", porque nunca se prometió crearlo.
                var motivoCentro = centrosDeclaradosEnElArchivo.TryGetValue(fila.NombreCentro, out var existiaAlAnalizar)
                    ? existiaAlAnalizar
                        ? $"El centro «{fila.NombreCentro}» existía al analizar el archivo pero ya no existe al confirmar la importación; sin él la asignación no puede crearse."
                        : $"El centro «{fila.NombreCentro}» venía en la hoja Centros_Plataformas de este archivo pero no pudo crearse al importarlo (ahora requiere una Empresa asociada que esta plantilla no recoge); créalo manualmente en Centros y vuelve a importar para que esta asignación se registre."
                    : $"El centro «{fila.NombreCentro}» no existe en el sistema y este archivo no lo declara en la hoja Centros_Plataformas; sin él la asignación no puede crearse.";

                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Asignaciones", 0, $"{fila.Dni} — {fila.NombreCentro}", motivoCentro));
                continue;
            }

            var repetidaEnElArchivo = !clavesAsignacionesDelPlan.Add((trabajadorId, centroId));
            var clave = (trabajadorId, centroId);

            if (clavesAsignacionesActivas.Contains(clave))
            {
                if (repetidaEnElArchivo)
                    // El propio archivo la trae dos veces (el plan prometió dos altas
                    // y solo cabe una): se registra para que los contadores cuadren,
                    // con su causal, no con la de la reaparición entre pasos.
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        "Asignaciones", 0, $"{fila.Dni} — {fila.NombreCentro}",
                        $"Esta asignación del trabajador {fila.Dni} al centro «{fila.NombreCentro}» ya venía antes en este mismo archivo; se registra una sola vez."));
                else if (!fila.YaExiste)
                    // Reutilización que el análisis NO llegó a anunciar (ver nota de la clase).
                    omitidosEnEscritura.Add(new ItemImportacionDto(
                        "Asignaciones", 0, $"{fila.Dni} — {fila.NombreCentro}",
                        $"El trabajador {fila.Dni} ya estaba asignado al centro «{fila.NombreCentro}» al confirmar la importación, aunque no lo estaba al analizar el archivo; se conserva la asignación activa que ya había en vez de duplicarla."));
                continue;
            }

            // DEC-19: la nueva alta no puede pisar el rango de una asignación
            // YA CERRADA del mismo trío — a diferencia del caso de arriba,
            // aquí no hay nada que "reutilizar"; es una contradicción de
            // datos y se omite con su propia causal, nunca en silencio
            // (DCR-12 B). Comprobado antes de intentar crear, no dentro del
            // catch de ArgumentException: el dominio no conoce el histórico,
            // solo el repositorio puede detectarlo.
            if (clavesAsignacionesSolapanConCerrada.Contains(clave))
            {
                omitidosEnEscritura.Add(new ItemImportacionDto(
                    "Asignaciones", 0, $"{fila.Dni} — {fila.NombreCentro}",
                    $"El trabajador {fila.Dni} tuvo antes una asignación al centro «{fila.NombreCentro}» cuyo periodo de vigencia se solapa con la fecha de alta de hoy; revisa las fechas del historial antes de reintentar."));
                continue;
            }

            try
            {
                var asignacion = new Asignacion(trabajadorId, centroId, hoy);
                asignacionRepositorio.Agregar(asignacion);
                // Marca el par como ocupado para que una fila repetida más
                // adelante en este mismo archivo caiga en la rama de arriba
                // ("ya venía antes en este mismo archivo") en vez de crear una
                // segunda asignación activa para el mismo trío.
                clavesAsignacionesActivas.Add(clave);
                asignacionesCreadas++;
            }
            catch (ArgumentException ex)
            {
                omitidosEnEscritura.Add(new ItemImportacionDto("Asignaciones", 0, $"{fila.Dni} — {fila.NombreCentro}", ex.Message));
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return new ResultadoImportacionDto(
            clientesCreados, centrosCreados, empresasCreadas, trabajadoresCreados, documentosCreados, asignacionesCreadas,
            plan.Advertencias, [.. plan.Omitidos, .. omitidosEnEscritura]);
    }
}
