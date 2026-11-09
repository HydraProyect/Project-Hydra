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
using System.Data.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

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
    IMediator mediator, RevisionPlanAsistente revision, ModificacionTareaAsistente modificacion, ITenantActual tenantActual,
    ITransaccionDeComando transaccion)
    : IRequestHandler<EjecutarPlanTareaAsistenteCommand, Result<EjecucionPlanAsistenteDto>>
{
    private const int LongitudMaximaMotivo = 500;

    /// <summary>SQLSTATE estándar de violación de unicidad (unique_violation).</summary>
    private const string ViolacionDeUnicidad = "23505";

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

        // Los pasos tienen que ser exactamente los del catálogo, en su orden: lo guardado en
        // la Tarea no decide qué ni cuántas operaciones se ejecutan.
        var orden = CatalogoOrdenesAsistente.PorId(primero.OrdenAsistenteId);
        var operaciones = pasos.Select(Operacion).ToList();
        if (orden is null || !EjecucionPlanAsistente.SabeEjecutar(orden.Id)
            || pasos.Any(p => p.OrdenAsistenteId != orden.Id)
            || !operaciones.SequenceEqual(orden.Ejecucion.Select(e => e.Operacion.Name), StringComparer.Ordinal))
            return Result.Fallo<EjecucionPlanAsistenteDto>(Error.Crear(
                "AsistenteIa.PlanAlterado", "El plan guardado no coincide con la gestión del catálogo: descártalo y vuelve a pedirlo."));

        var valores = PlanPropuestoATareaAsistente.LeerValores(primero.DatosJson);
        var revisado = await revision.RevisarAsync(primero.OrdenAsistenteId, 0, valores, cancellationToken);
        if (revisado.EsFallido)
            return Result.Fallo<EjecucionPlanAsistenteDto>(revisado.Error);

        // Lo que impedía confirmar impide también ejecutar (el JSON guardado no se da por
        // bueno). Salvo el documento repetido del Trabajador que este mismo plan ya dio de
        // alta: es el suyo, y su Asignación tiene que poder reintentarse.
        var trabajadorYaCreado = pasos.Any(p =>
            Operacion(p) == nameof(CrearTrabajadorCommand) && p.Estado == EstadoPasoTareaAsistente.Ejecutado);
        if ((revisado.Valor.Plan.Avisos ?? []).FirstOrDefault(a =>
                a.Gravedad == GravedadAvisoPasoTareaAsistente.Bloqueante
                && !(trabajadorYaCreado && a.Campo == TiposDatoPlan.CampoTrabajador)) is { } bloqueante)
            return Result.Fallo<EjecucionPlanAsistenteDto>(Error.Crear("AsistenteIa.PlanBloqueado", bloqueante.Texto));

        var datos = revisado.Valor.Plan.Datos.ToDictionary(d => d.Campo, StringComparer.Ordinal);
        var opciones = revisado.Valor.Opciones;

        var resultados = new List<PasoEjecutadoDto>();
        Guid? trabajadorCreado = null;
        var parado = false;
        foreach (var paso in pasos)
        {
            var operacion = Operacion(paso);

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

            var intento = await EjecutarPasoAsync(request.TareaId, paso.Id, operacion, datos, opciones, trabajadorCreado, cancellationToken);
            if (intento.Conflicto is { } conflicto)
                return Result.Fallo<EjecucionPlanAsistenteDto>(conflicto);

            if (intento.EntidadId is { } id)
            {
                if (operacion == nameof(CrearTrabajadorCommand))
                    trabajadorCreado = id;
                resultados.Add(new PasoEjecutadoDto(paso.Id, paso.Posicion, operacion, EstadoPasoTareaAsistente.Ejecutado, id, null));
                continue;
            }

            var motivo = intento.Fallo!.Mensaje;
            if (motivo.Length > LongitudMaximaMotivo)
                motivo = motivo[..LongitudMaximaMotivo];
            var ahora = DateTime.UtcNow;
            // Fuera de la transacción del paso, que se deshizo entera. Si mientras tanto otra
            // ventana lo ejecutó, no se pisa su resultado: se informa de él.
            var hechoPorOtra = false;
            Guid? hecho = null;
            var registro = await modificacion.AplicarAsync(request.TareaId, null, (t, _) =>
            {
                var guardado = t.Pasos.Single(p => p.Id == paso.Id);
                if (guardado.Estado == EstadoPasoTareaAsistente.Ejecutado)
                {
                    hechoPorOtra = true;
                    hecho = guardado.EntidadResultadoId;
                }
                else
                    t.RegistrarPasoFallido(paso.Id, motivo, ahora);
            }, cancellationToken);
            if (registro.EsFallido)
                return Result.Fallo<EjecucionPlanAsistenteDto>(registro.Error);
            if (hechoPorOtra)
            {
                if (operacion == nameof(CrearTrabajadorCommand))
                    trabajadorCreado = hecho;
                resultados.Add(new PasoEjecutadoDto(paso.Id, paso.Posicion, operacion, EstadoPasoTareaAsistente.Ejecutado, hecho, null));
                // Sin el Id de lo que hizo la otra ventana no se sigue: el paso siguiente no
                // sabría sobre qué entidad trabajar.
                parado = hecho is null;
                continue;
            }
            resultados.Add(new PasoEjecutadoDto(paso.Id, paso.Posicion, operacion, EstadoPasoTareaAsistente.Fallido, null, motivo));
            parado = true;
        }

        return new EjecucionPlanAsistenteDto(
            resultados.All(r => r.Estado == EstadoPasoTareaAsistente.Ejecutado), resultados);
    }

    private static string Operacion(PasoTareaAsistenteDto paso) =>
        PlanPropuestoATareaAsistente.LeerOperacion(paso.DatosJson)
        ?? CatalogoOrdenesAsistente.PorId(paso.OrdenAsistenteId)?.Ejecucion.SingleOrDefault()?.Operacion.Name
        ?? string.Empty;

    /// <summary>Lo que dejó un paso: el Id creado, el fallo del Command o un conflicto con otra ejecución.</summary>
    private sealed record IntentoPaso(Guid? EntidadId, Error? Fallo, Error? Conflicto);

    /// <summary>
    /// El efecto del Command y su registro en la Tarea van en <b>una sola transacción</b>:
    /// o quedan los dos o ninguno, así que un corte entre ambos no deja una Visita creada
    /// con el paso pendiente de repetirse.
    /// <para>
    /// También es el reclamo del paso frente a otra ventana del mismo Actor real: dentro
    /// de la transacción se vuelve a leer la Tarea, se comprueba que el paso sigue sin
    /// ejecutar y se registra con la versión leída. La versión es token de concurrencia,
    /// así que de dos ejecuciones simultáneas solo confirma la primera: la segunda choca al
    /// registrar y deshace su propio efecto.
    /// </para>
    /// </summary>
    private async Task<IntentoPaso> EjecutarPasoAsync(
        Guid tareaId, Guid pasoId, string operacion, IReadOnlyDictionary<string, Queries.ProponerPlan.DatoPropuestoDto> datos,
        OpcionesPlanAsistenteDto opciones, Guid? trabajadorCreado, CancellationToken cancellationToken)
    {
        Guid? creado = null;
        Error? fallo = null;
        try
        {
            var hecho = await transaccion.EjecutarAsync(async ct =>
            {
                creado = null;
                fallo = null;
                var actual = await mediator.Send(new ObtenerTareaAsistenteQuery(tareaId), ct);
                if (actual.EsFallido)
                    return Result.Fallo(actual.Error);
                if (actual.Valor.Pasos.SingleOrDefault(p => p.Id == pasoId) is not
                    { Estado: EstadoPasoTareaAsistente.Confirmado or EstadoPasoTareaAsistente.Fallido })
                    return Result.Fallo(PasoEnCurso());

                Result<Guid> ejecutado;
                try
                {
                    ejecutado = await EnviarAsync(operacion, datos, opciones, trabajadorCreado, ct);
                }
                catch (ValidationException excepcion)
                {
                    ejecutado = Result.Fallo<Guid>(Error.Crear("AsistenteIa.PasoNoValido",
                        excepcion.Errors.Select(e => e.ErrorMessage).FirstOrDefault() ?? "El paso no es válido."));
                }
                if (ejecutado.EsFallido)
                {
                    fallo = ejecutado.Error;
                    return Result.Fallo(ejecutado.Error);
                }

                var id = ejecutado.Valor;
                var ahora = DateTime.UtcNow;
                var registro = await modificacion.AplicarAsync(tareaId, actual.Valor.Version,
                    (t, _) => t.RegistrarPasoEjecutado(pasoId, id, ahora), ct);
                if (registro.EsFallido)
                    return registro;
                creado = id;
                return Result.Exito();
            }, cancellationToken);

            if (hecho.EsExitoso && creado is { } entidad)
                return new IntentoPaso(entidad, null, null);
            return fallo is not null ? new IntentoPaso(null, fallo, null) : new IntentoPaso(null, null, hecho.Error);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new IntentoPaso(null, null, PasoEnCurso());
        }
        catch (DbUpdateException excepcion) when (excepcion.InnerException is DbException { SqlState: ViolacionDeUnicidad })
        {
            // Solo el choque con un índice único (p. ej. el del documento del Trabajador, cuando
            // otra ventana lo dio de alta a la vez): cualquier otro rechazo de la base, como una
            // denegación de RLS, no es una carrera y se propaga. La transacción ya deshizo el
            // efecto; el paso queda fallido salvo que otra ventana lo haya ejecutado, y eso lo
            // decide el registro del fallo, que relee la Tarea.
            return new IntentoPaso(null, Error.Crear("AsistenteIa.PasoNoGuardado",
                "No se ha podido guardar este paso: puede que ya exista o que otra ventana lo esté haciendo. Vuelve a abrir el plan y reintenta."), null);
        }
    }

    private static Error PasoEnCurso() => Error.Crear("AsistenteIa.PasoEnCurso",
        "Otra ventana está ejecutando este plan: espera un momento y vuelve a abrirlo.");

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
