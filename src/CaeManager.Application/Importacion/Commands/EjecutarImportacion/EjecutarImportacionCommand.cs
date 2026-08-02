using CaeManager.Application.Common;
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

public class EjecutarImportacionCommandHandler(
    IEmpresaRepository empresaRepositorio,
    ITrabajadorRepository trabajadorRepositorio,
    IDocumentoRepository documentoRepositorio,
    IAsignacionRepository asignacionRepositorio,
    IApplicationDbContext dbContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EjecutarImportacionCommand, Result<ResultadoImportacionDto>>
{
    public async Task<Result<ResultadoImportacionDto>> Handle(EjecutarImportacionCommand request, CancellationToken cancellationToken)
    {
        var plan = request.Plan;
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var clientesPorNombre = await dbContext.Clientes.ToDictionaryAsync(
            c => c.RazonSocial, c => c.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var centrosPorNombre = await dbContext.Centros.ToDictionaryAsync(
            c => c.Nombre, c => c.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var empresasPorRazonSocial = await dbContext.Empresas.ToDictionaryAsync(
            e => e.RazonSocial, e => e.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var trabajadoresPorDni = await dbContext.Trabajadores.ToDictionaryAsync(
            t => t.Dni, t => t.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var tiposDocumentoPorNombre = await dbContext.TiposDocumento.ToDictionaryAsync(
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
            if (empresasPorRazonSocial.ContainsKey(fila.RazonSocial)) continue;

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
            if (trabajadoresPorDni.ContainsKey(fila.Dni)) continue;
            if (!empresasPorRazonSocial.TryGetValue(fila.RazonSocialEmpresa, out var empresaId)) continue;

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

        var documentosExistentes = await dbContext.Documentos
            .Select(d => new { d.TrabajadorId, d.TipoDocumentoId })
            .ToListAsync(cancellationToken);
        var clavesDocumentosExistentes = documentosExistentes
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        foreach (var fila in plan.Documentos)
        {
            if (!trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorId)) continue;
            if (!tiposDocumentoPorNombre.TryGetValue(fila.NombreTipoDocumento, out var tipoDocumento)) continue;
            if (!clavesDocumentosExistentes.Add((trabajadorId, tipoDocumento.Id))) continue;

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

        var asignacionesActivasExistentes = await dbContext.Asignaciones
            .Where(a => a.FechaBaja == null)
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);
        var clavesAsignacionesActivas = asignacionesActivasExistentes
            .Select(a => (a.TrabajadorId, a.CentroId))
            .ToHashSet();

        foreach (var fila in plan.Asignaciones)
        {
            if (!trabajadoresPorDni.TryGetValue(fila.Dni, out var trabajadorId)) continue;
            if (!centrosPorNombre.TryGetValue(fila.NombreCentro, out var centroId)) continue;
            if (!clavesAsignacionesActivas.Add((trabajadorId, centroId))) continue;

            try
            {
                var asignacion = new Asignacion(trabajadorId, centroId, hoy);
                asignacionRepositorio.Agregar(asignacion);
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
