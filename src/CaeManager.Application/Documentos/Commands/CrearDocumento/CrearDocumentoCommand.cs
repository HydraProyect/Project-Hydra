using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.CrearDocumento;

/// <summary>
/// Exactamente uno de TrabajadorId/ClienteId/EmpresaId/VehiculoId/ProyectoId debe venir
/// informado — el propietario del Documento (ver <see cref="Documento.DeTrabajador"/>/
/// <see cref="Documento.DeCliente"/>/<see cref="Documento.DeEmpresa"/>/
/// <see cref="Documento.DeVehiculo"/>/<see cref="Documento.DeProyecto"/>), y debe coincidir con el
/// <see cref="AmbitoAplicacion"/> del TipoDocumento elegido.
/// FechaVencimientoManual solo se usa cuando el TipoDocumento no
/// tiene vencimiento automático (AplicaVencimientoAutomatico = false) — en
/// ese caso no hay vigencia en meses que calcular, así que se acepta la
/// fecha que introduce el usuario. Si el tipo sí es automático, se ignora y
/// se recalcula siempre a partir de la vigencia en meses.
/// </summary>
public record CrearDocumentoCommand(
    Guid? TrabajadorId, Guid? ClienteId, Guid? EmpresaId, Guid? VehiculoId, Guid? ProyectoId, Guid TipoDocumentoId, DateOnly FechaEmision,
    DateOnly? FechaVencimientoManual, string? ArchivoUrl, string? Comentarios)
    : ICommand<Guid>;

public class CrearDocumentoCommandValidator : AbstractValidator<CrearDocumentoCommand>
{
    public CrearDocumentoCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => new[] { c.TrabajadorId, c.ClienteId, c.EmpresaId, c.VehiculoId, c.ProyectoId }.Count(id => id is not null) == 1)
            .WithMessage("Selecciona un trabajador, un cliente, una empresa, un vehículo o un proyecto (exactamente uno).");

        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona un tipo de documento.");
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }
}

public class CrearDocumentoCommandHandler(
    IDocumentoRepository repositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IClientesQueryContext clientesContext,
    IVehiculosQueryContext vehiculosContext,
    IProyectosQueryContext proyectosContext,
    IEmpresasQueryContext empresasContext,
    IUnitOfWork unitOfWork,
    ITrabajoAnalisisDocumentoRepository colaAnalisis,
    ICurrentUserService currentUserService,
    IDerivarCanalesAplicablesDocumentoService derivarCanalesAplicables,
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio)
    : IRequestHandler<CrearDocumentoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearDocumentoCommand request, CancellationToken cancellationToken)
    {
        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == request.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null)
            return Result.Fallo<Guid>(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        var ambitoSolicitado = request.TrabajadorId is not null ? AmbitoAplicacion.Trabajador
            : request.ClienteId is not null ? AmbitoAplicacion.Cliente
            : request.VehiculoId is not null ? AmbitoAplicacion.Vehiculo
            : request.ProyectoId is not null ? AmbitoAplicacion.Proyecto
            : AmbitoAplicacion.Empresa;

        if (tipoDocumento.AmbitoAplicacion != ambitoSolicitado)
            return Result.Fallo<Guid>(Error.Crear(
                "Documento.AmbitoIncorrecto",
                $"\"{tipoDocumento.Nombre}\" es un tipo de documento de {DescribirAmbito(tipoDocumento.AmbitoAplicacion)}, no de {DescribirAmbito(ambitoSolicitado)}."));

        // Verificación del propietario (P0-1 de docs/business/MATURITY_REVIEW.md):
        // sin esto, un Id de otro tenant se persistía sin error, sellado con
        // el tenant actual — hallazgo explícito del comité sobre este mismo
        // handler. El filtro global de EF ya deja "no encontrado" un Id ajeno.
        var propietarioEncontrado = ambitoSolicitado switch
        {
            AmbitoAplicacion.Trabajador => await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == request.TrabajadorId, cancellationToken),
            AmbitoAplicacion.Cliente => await clientesContext.Clientes.AnyAsync(c => c.Id == request.ClienteId, cancellationToken),
            AmbitoAplicacion.Vehiculo => await vehiculosContext.Vehiculos.AnyAsync(v => v.Id == request.VehiculoId, cancellationToken),
            AmbitoAplicacion.Proyecto => await proyectosContext.Proyectos.AnyAsync(p => p.Id == request.ProyectoId, cancellationToken),
            _ => await empresasContext.Empresas.AnyAsync(e => e.Id == request.EmpresaId, cancellationToken)
        };

        if (!propietarioEncontrado)
        {
            var mensaje = ambitoSolicitado switch
            {
                AmbitoAplicacion.Trabajador => "No encontramos este trabajador.",
                AmbitoAplicacion.Cliente => "No encontramos este cliente.",
                AmbitoAplicacion.Vehiculo => "No encontramos este vehículo.",
                AmbitoAplicacion.Proyecto => "No encontramos este proyecto.",
                _ => "No encontramos esta empresa."
            };
            return Result.Fallo<Guid>(Error.Crear("Documento.PropietarioNoEncontrado", mensaje));
        }

        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(request.FechaEmision, tipoDocumento.VigenciaMeses)
            : request.FechaVencimientoManual;

        var documento = ambitoSolicitado switch
        {
            AmbitoAplicacion.Trabajador => Documento.DeTrabajador(
                request.TrabajadorId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            AmbitoAplicacion.Cliente => Documento.DeCliente(
                request.ClienteId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            AmbitoAplicacion.Vehiculo => Documento.DeVehiculo(
                request.VehiculoId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            AmbitoAplicacion.Proyecto => Documento.DeProyecto(
                request.ProyectoId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            _ => Documento.DeEmpresa(
                request.EmpresaId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios)
        };

        repositorio.Agregar(documento);

        // Acreditación por plataforma destino (docs/ux-audit/PLAN-EJECUCION-UX.md
        // § Parte 2 (b)/Lote 2-D): al nacer el Documento, se derivan los accesos
        // de plataforma que hoy le aplican (Trabajador/Empresa → asignaciones
        // activas → centro → canal) y se crea una AcreditacionDocumentoPlataforma
        // por cada uno, en Pendiente de subir. Mismo SaveChangesAsync que el
        // Documento — o se confirman juntas o ninguna.
        var canalesAplicables = await derivarCanalesAplicables.ObtenerCanalGestionDocumentalIdsAplicablesAsync(documento, cancellationToken);
        foreach (var canalId in canalesAplicables)
            acreditacionRepositorio.Agregar(new AcreditacionDocumentoPlataforma(documento.Id, canalId));

        // Los dos análisis pesados se encolan en vez de ejecutarse aquí: son
        // llamadas a un modelo externo, con su latencia, y hacerlas dentro del
        // Command dejaba el circuito de Blazor bloqueado mientras el usuario
        // esperaba a que "terminara de subir" algo que en realidad ya estaba
        // guardado.
        //
        // El trabajo se agrega al mismo SaveChangesAsync que el Documento —
        // no en una llamada aparte después — para que los dos se confirmen
        // juntos o ninguno: antes, un fallo justo entre el guardado del
        // Documento y el encolado en memoria perdía el encargo sin que nadie
        // se enterase, y el propio proceso reiniciándose con encargos
        // pendientes ya los perdía siempre (cola en memoria). No cambia
        // ninguna garantía de negocio: el análisis en sí sigue siendo mejor
        // esfuerzo — un fallo se reintenta unas pocas veces y luego se marca
        // como definitivamente fallido, nunca invalida el Documento ya
        // guardado. Al terminar, el procesador avisa por la campana.
        var necesitaDeteccion = ambitoSolicitado == AmbitoAplicacion.Empresa
            && tipoDocumento.DeteccionTrabajadoresActiva && !string.IsNullOrWhiteSpace(request.ArchivoUrl);
        var necesitaVerificacion = ambitoSolicitado == AmbitoAplicacion.Trabajador
            && tipoDocumento.VerificacionIaActiva && !string.IsNullOrWhiteSpace(request.ArchivoUrl);
        // Sin restricción de ámbito adicional: el perfil solo está asignado en
        // tipos de Empresa/Cliente (ver TipoDocumento.PerfilDocumentoOficial).
        var necesitaValidacionOficial = tipoDocumento.PerfilDocumentoOficial != PerfilDocumentoOficial.Ninguno
            && !string.IsNullOrWhiteSpace(request.ArchivoUrl);

        if (necesitaDeteccion || necesitaVerificacion || necesitaValidacionOficial)
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();

            if (necesitaDeteccion)
                colaAnalisis.Agregar(new TrabajoAnalisisDocumento(documento.Id, usuarioId, TipoAnalisisDocumento.DeteccionTrabajadores));

            if (necesitaVerificacion)
                colaAnalisis.Agregar(new TrabajoAnalisisDocumento(documento.Id, usuarioId, TipoAnalisisDocumento.VerificacionIa));

            if (necesitaValidacionOficial)
                colaAnalisis.Agregar(new TrabajoAnalisisDocumento(documento.Id, usuarioId, TipoAnalisisDocumento.VerificacionFirmaDigital));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(documento.Id);
    }

    private static string DescribirAmbito(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Trabajador => "Trabajador",
        AmbitoAplicacion.Cliente => "Cliente",
        AmbitoAplicacion.Vehiculo => "Vehículo",
        AmbitoAplicacion.Proyecto => "Proyecto",
        _ => "Empresa"
    };
}
