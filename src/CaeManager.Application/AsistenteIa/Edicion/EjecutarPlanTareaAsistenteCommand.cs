using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.AsistenteIa.Tareas.Queries;
using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Edicion;

/// <summary>
/// Ejecuta el plan confirmado de una Tarea del asistente, paso a paso, <b>por los
/// Commands del catálogo</b> (<see cref="OrdenAsistida.Ejecucion"/>) enviados por
/// MediatR: cada uno pasa por su propia autorización de escritura, su validación, su
/// comprobación de Asignación de Cartera y la RLS del Tenant efectivo. Vega no tiene
/// una vía propia de escritura.
/// <para>
/// Solo en el Tenant efectivo de la sesión, y solo si es el Tenant destino guardado en
/// el plan: nunca se escribe en otro Tenant con un ámbito explícito. Antes de ejecutar
/// se vuelven a revisar los valores contra la cartera (<see cref="RevisionPlanAsistente"/>),
/// por si la cartera cambió entre la confirmación y la ejecución.
/// </para>
/// <para>
/// Idempotente por paso: un paso ya ejecutado no se repite (su resultado se lee de
/// <see cref="PasoTareaAsistente.EntidadResultadoId"/>, que es como la Asignación sabe
/// qué Trabajador acaba de darse de alta) y el primero que falla para la ejecución y
/// queda para reintentar. Devuelve siempre el estado de cada paso.
/// </para>
/// </summary>
public record EjecutarPlanTareaAsistenteCommand(Guid TareaId) : ICommand<EjecucionPlanAsistenteDto>;

public record PasoEjecutadoDto(
    Guid PasoId, int Posicion, string Operacion, EstadoPasoTareaAsistente Estado, Guid? EntidadResultadoId, string? Motivo);

public record EjecucionPlanAsistenteDto(bool Terminada, IReadOnlyList<PasoEjecutadoDto> Pasos);

public class EjecutarPlanTareaAsistenteCommandValidator : AbstractValidator<EjecutarPlanTareaAsistenteCommand>
{
    public EjecutarPlanTareaAsistenteCommandValidator() => RuleFor(c => c.TareaId).NotEmpty();
}

public class EjecutarPlanTareaAsistenteCommandHandler(
    IMediator mediator, RevisionPlanAsistente revision, ModificacionTareaAsistente modificacion, ITenantActual tenantActual)
    : IRequestHandler<EjecutarPlanTareaAsistenteCommand, Result<EjecucionPlanAsistenteDto>>
{
    private const int LongitudMaximaMotivo = 500;

    public async Task<Result<EjecucionPlanAsistenteDto>> Handle(
        EjecutarPlanTareaAsistenteCommand request, CancellationToken cancellationToken)
    {
        var tarea = await mediator.Send(new ObtenerTareaAsistenteQuery(request.TareaId), cancellationToken);
        if (tarea.EsFallido)
            return Result.Fallo<EjecucionPlanAsistenteDto>(tarea.Error);

        var pasos = tarea.Valor.Pasos.Where(p => p.Estado != EstadoPasoTareaAsistente.Descartado).OrderBy(p => p.Posicion).ToList();
        if (tarea.Valor.Estado == EstadoTareaAsistente.Terminada)
            return new EjecucionPlanAsistenteDto(true, pasos.Select(Resultado).ToList());
        if (tarea.Valor.Estado != EstadoTareaAsistente.Confirmada || pasos.Count == 0)
            return Result.Fallo<EjecucionPlanAsistenteDto>(Error.Crear(
                "AsistenteIa.PlanSinConfirmar", "Solo se ejecuta un plan confirmado."));

        var primero = pasos[0];
        if (tenantActual.TenantId is not { } tenantEfectivo
            || PlanPropuestoATareaAsistente.LeerTenantDestino(primero.DatosJson) != tenantEfectivo)
            return Result.Fallo<EjecucionPlanAsistenteDto>(Error.Crear(
                "AsistenteIa.DestinoNoEsLaPantalla", PlanPropuestoATareaAsistente.TextoDestinoNoEsLaPantalla));

        var valores = PlanPropuestoATareaAsistente.LeerValores(primero.DatosJson);
        var revisado = await revision.RevisarAsync(primero.OrdenAsistenteId, 0, valores, cancellationToken);
        if (revisado.EsFallido)
            return Result.Fallo<EjecucionPlanAsistenteDto>(revisado.Error);
        var datos = revisado.Valor.Plan.Datos.ToDictionary(d => d.Campo, StringComparer.Ordinal);
        var opciones = revisado.Valor.Opciones;

        var resultados = new List<PasoEjecutadoDto>();
        Guid? trabajadorCreado = null;
        var parado = false;
        foreach (var paso in pasos)
        {
            var operacion = PlanPropuestoATareaAsistente.LeerOperacion(paso.DatosJson)
                ?? CatalogoOrdenesAsistente.PorId(paso.OrdenAsistenteId)?.Ejecucion.SingleOrDefault()?.Operacion.Name
                ?? string.Empty;

            if (paso.Estado == EstadoPasoTareaAsistente.Ejecutado)
            {
                if (operacion == nameof(CrearTrabajadorCommand))
                    trabajadorCreado = paso.EntidadResultadoId;
                resultados.Add(Resultado(paso) with { Operacion = operacion });
                continue;
            }

            if (parado || paso.Estado is not (EstadoPasoTareaAsistente.Confirmado or EstadoPasoTareaAsistente.Fallido))
            {
                resultados.Add(Resultado(paso) with { Operacion = operacion });
                continue;
            }

            var ejecutado = await EnviarAsync(operacion, datos, opciones, trabajadorCreado, cancellationToken);
            var ahora = DateTime.UtcNow;
            if (ejecutado.EsExitoso)
            {
                var id = ejecutado.Valor;
                var registro = await modificacion.AplicarAsync(request.TareaId, null,
                    (t, _) => t.RegistrarPasoEjecutado(paso.Id, id, ahora), cancellationToken);
                if (registro.EsFallido)
                    return Result.Fallo<EjecucionPlanAsistenteDto>(registro.Error);
                if (operacion == nameof(CrearTrabajadorCommand))
                    trabajadorCreado = id;
                resultados.Add(new PasoEjecutadoDto(paso.Id, paso.Posicion, operacion, EstadoPasoTareaAsistente.Ejecutado, id, null));
            }
            else
            {
                var motivo = ejecutado.Error.Mensaje;
                if (motivo.Length > LongitudMaximaMotivo)
                    motivo = motivo[..LongitudMaximaMotivo];
                var registro = await modificacion.AplicarAsync(request.TareaId, null,
                    (t, _) => t.RegistrarPasoFallido(paso.Id, motivo, ahora), cancellationToken);
                if (registro.EsFallido)
                    return Result.Fallo<EjecucionPlanAsistenteDto>(registro.Error);
                resultados.Add(new PasoEjecutadoDto(paso.Id, paso.Posicion, operacion, EstadoPasoTareaAsistente.Fallido, null, motivo));
                parado = true;
            }
        }

        return new EjecucionPlanAsistenteDto(
            resultados.All(r => r.Estado == EstadoPasoTareaAsistente.Ejecutado), resultados);
    }

    private static PasoEjecutadoDto Resultado(PasoTareaAsistenteDto paso) =>
        new(paso.Id, paso.Posicion, PlanPropuestoATareaAsistente.LeerOperacion(paso.DatosJson) ?? string.Empty,
            paso.Estado, paso.EntidadResultadoId, paso.MotivoFallo);

    /// <summary>
    /// Construye el Command real de la operación y lo envía. Solo se construye con lo
    /// que <see cref="RevisionPlanAsistente"/> acaba de dar por bueno.
    /// </summary>
    private async Task<Result<Guid>> EnviarAsync(
        string operacion, IReadOnlyDictionary<string, Queries.ProponerPlan.DatoPropuestoDto> datos,
        OpcionesPlanAsistenteDto opciones, Guid? trabajadorCreado, CancellationToken cancellationToken)
    {
        Guid? Id(string campo) => datos.GetValueOrDefault(campo)?.CandidatoId;
        ValorDatoPlanDto? Valor(string campo) => datos.GetValueOrDefault(campo)?.Valor;
        string? Texto(string campo) => Valor(campo)?.Texto;

        switch (operacion)
        {
            case nameof(CrearTrabajadorCommand):
                {
                    if (Valor(TiposDatoPlan.CampoTrabajador)?.Trabajador is not { Completo: true } persona
                        || Id(ObtenerCandidatosAsistenteQueryHandler.CampoEmpleador) is not { } empleadorId)
                        return Falta();
                    var esSubcontrata = opciones.Buscar(ObtenerCandidatosAsistenteQueryHandler.CampoEmpleador, empleadorId)?.EsSubcontrata == true;
                    return await mediator.Send(new CrearTrabajadorCommand(
                        esSubcontrata ? null : empleadorId, esSubcontrata ? empleadorId : null,
                        persona.Nombre, persona.Apellidos, persona.Documento,
                        FechaNacimiento: null, Email: null, Observaciones: null), cancellationToken);
                }

            case nameof(CrearAsignacionCommand):
                {
                    if (trabajadorCreado is not { } trabajadorId
                        || Id(ObtenerCandidatosAsistenteQueryHandler.CampoCentro) is not { } centroId
                        || Valor(TiposDatoPlan.CampoFechaInicio)?.Fecha is not { } alta)
                        return Falta();
                    return await mediator.Send(new CrearAsignacionCommand(trabajadorId, centroId, alta), cancellationToken);
                }

            case nameof(CrearVisitaCommand):
                {
                    if (Id(ObtenerCandidatosAsistenteQueryHandler.CampoCentro) is not { } centroId
                        || Valor(TiposDatoPlan.CampoFechaInicio)?.Fecha is not { } inicio
                        || Valor(TiposDatoPlan.CampoFechaFin)?.Fecha is not { } fin
                        || Valor(ObtenerCandidatosAsistenteQueryHandler.CampoTrabajadores)?.Ids is not { Count: > 0 } personas)
                        return Falta();
                    return await mediator.Send(new CrearVisitaCommand(
                        centroId, inicio, fin, personas, Texto(TiposDatoPlan.CampoMotivo)), cancellationToken);
                }

            case nameof(CrearCentroCommand):
                {
                    if (Id(ObtenerCandidatosAsistenteQueryHandler.CampoClienteEmpresarial) is not { } clienteEmpresarialId
                        || Id(ObtenerCandidatosAsistenteQueryHandler.CampoEmpresa) is not { } empresaId
                        || Texto(TiposDatoPlan.CampoNombre) is not { } nombre)
                        return Falta();
                    return await mediator.Send(new CrearCentroCommand(
                        clienteEmpresarialId, empresaId, nombre, CodigoCentro: null, Direccion: Texto(TiposDatoPlan.CampoDireccion),
                        Contacto: null, ContratoVigenteHasta: null), cancellationToken);
                }

            case nameof(CrearClienteCommand):
                {
                    if (Texto(TiposDatoPlan.CampoRazonSocial) is not { } razonSocial
                        || Texto(TiposDatoPlan.CampoCif) is not { } cif
                        || Valor(TiposDatoPlan.CampoEsCritico)?.SiNo is not { } esCritico)
                        return Falta();
                    return await mediator.Send(new CrearClienteCommand(razonSocial, cif, esCritico, Notas: null), cancellationToken);
                }

            default:
                return Result.Fallo<Guid>(Error.Crear("AsistenteIa.OperacionDesconocida",
                    EjecucionPlanAsistente.TextoNoLaHaceVega));
        }
    }

    private static Result<Guid> Falta() =>
        Result.Fallo<Guid>(Error.Crear("AsistenteIa.PlanIncompleto", "Al plan le falta un dato para este paso."));
}
