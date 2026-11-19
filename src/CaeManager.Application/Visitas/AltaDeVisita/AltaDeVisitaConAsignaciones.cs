using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Visitas;

namespace CaeManager.Application.Visitas.AltaDeVisita;

/// <summary>Lo que el <c>agregarVisita</c> de <see cref="IAltaDeVisitaConAsignaciones.EjecutarAsync"/> deja agregado para guardar: la Visita y, si nació de un mensaje, su Id.</summary>
public readonly record struct VisitaAgregada(Guid VisitaId, Guid? MensajeOrigenId);

/// <summary>
/// La parte de <c>CrearVisitaCommand</c> que asigna y crea Gestiones (decisión de Chris, 2026-11-19): en un solo guardado, la
/// Visita, una Asignación Trabajador–Centro por cada Trabajador que no la tenía activa y las Gestiones de
/// <see cref="ReglaGestionesDeVisita"/>. Nunca la dispara Vega por su cuenta: solo corre cuando el Command llega con
/// <c>AsignarYCrearGestiones</c>, que pone la interfaz tras enseñar la vista previa.
/// </summary>
public interface IAltaDeVisitaConAsignaciones
{
    /// <param name="revalidar">
    /// Vuelve a comprobar, sobre lo que relee bajo el candado, el alcance del Centro y la admisibilidad de los Trabajadores,
    /// con la misma regla y la misma respuesta que el Command fuera del candado. Se llama antes de calcular el plan y de
    /// cualquier otro rechazo: quien perdió el alcance mientras esperaba recibe la respuesta de «no existe», no otra.
    /// </param>
    /// <param name="agregarVisita">
    /// Agrega la Visita (y lo que el Command guarda con ella) al contexto, sin guardar. Si falla, no se escribe nada.
    /// </param>
    /// <remarks>
    /// Los dos pueden llamarse más de una vez: la operación se reintenta con el contexto vacío. No se vuelven a llamar si la
    /// Visita que agregó un intento anterior ya está confirmada en la base: entonces se devuelve lo que escribió ese intento.
    /// </remarks>
    Task<Result<(VisitaAgregada Visita, PlanAltaVisitaDto Plan)>> EjecutarAsync(
        Guid centroId, DateOnly fechaFin, IReadOnlyList<Guid> trabajadorIds,
        Func<CancellationToken, Task<Result>> revalidar,
        Func<CancellationToken, Task<Result<VisitaAgregada>>> agregarVisita, CancellationToken cancellationToken);
}

public class AltaDeVisitaConAsignaciones(
    IPlanAltaVisitaService plan, IAutoridadAsignacionesService autoridad, IAsignacionRepository asignaciones,
    IGestionRepository gestiones, IAltaAcreditacionesPlataformaService altaAcreditaciones,
    ISerializacionAltasAcreditacionEmpresa serializacionAltas, IInvalidadorAlcance invalidadorAlcance, IUnitOfWork unitOfWork,
    IVisitaRepository visitas)
    : IAltaDeVisitaConAsignaciones
{
    public const string CodigoSinAutoridad = "Visita.SinAutoridadParaAsignar";
    public const string CodigoSolape = "Asignacion.SolapaConOtra";

    public async Task<Result<(VisitaAgregada Visita, PlanAltaVisitaDto Plan)>> EjecutarAsync(
        Guid centroId, DateOnly fechaFin, IReadOnlyList<Guid> trabajadorIds,
        Func<CancellationToken, Task<Result>> revalidar,
        Func<CancellationToken, Task<Result<VisitaAgregada>>> agregarVisita, CancellationToken cancellationToken)
    {
        VisitaAgregada visita = default;
        var resultado = PlanAltaVisitaDto.Vacio;
        var hoy = DiaDeNegocio.Hoy();

        // Crear una Asignación pone los Documentos del Trabajador ante los accesos de plataforma del Centro: va bajo el mismo
        // candado que CrearAsignacion (ISerializacionAltasAcreditacionEmpresa). Todo lo que decide se lee dentro, sobre lo
        // confirmado: qué Trabajadores ya están asignados, qué les falta, qué Gestiones están pendientes y la autoridad.
        var guardado = await serializacionAltas.EjecutarAsync(async ct =>
        {
            // Reintento tras un COMMIT que sí se confirmó pero se comunicó como error transitorio: la Visita que agregó el
            // intento anterior ya está en la base, y con ella sus Asignaciones y Gestiones (un solo guardado, una sola
            // transacción). Repetir crearía otra Visita con otro Id: se responde lo que ya se escribió, sin escribir nada.
            // Si no está, ese intento se deshizo entero y este empieza de cero. La consulta va a la base, no a lo rastreado.
            if (visita.VisitaId != Guid.Empty && await visitas.ObtenerPorIdAsync(visita.VisitaId, ct) is not null)
                return Result.Exito();

            // La Asignación de Cartera pudo revocarse mientras se esperaba el candado (issue #2118).
            invalidadorAlcance.Invalidar();

            // Lo que decide el Command (Centro dentro del alcance, Trabajadores admisibles), releído aquí dentro y antes que
            // nada: ningún rechazo posterior (autoridad, solape) habla de un Centro o de un Trabajador que ya no se alcanza.
            var revalidado = await revalidar(ct);
            if (revalidado.EsFallido)
                return revalidado;

            resultado = await plan.CalcularAsync(centroId, fechaFin, hoy, trabajadorIds, ct);
            var porAsignar = resultado.Asignaciones.Select(a => a.TrabajadorId).ToList();

            // Misma autoridad que CrearAsignacionCommand, sobre el Centro y cada Trabajador que se asigna. Ver al Trabajador
            // (TrabajadoresAdmisiblesEnVisita) no basta para asignarlo.
            if (!await AutoridadParaAsignar.TieneAsync(autoridad, centroId, porAsignar, ct))
                return Result.Fallo(Error.Crear(CodigoSinAutoridad, "No puedes dar de alta a estos trabajadores en este centro."));

            // DEC-19: un alta nueva no puede pisar el periodo de una Asignación ya cerrada del mismo par. El plan ya los
            // separa y la vista previa los enseña por su nombre. El mensaje del Command no los nombra: basta con que la vista
            // previa diga quiénes.
            if (resultado.Solapadas.Count > 0)
                return Result.Fallo(Error.Crear(CodigoSolape,
                    "Hay trabajadores que no se pueden asignar hoy a este centro: ya tuvieron una asignación a este centro cuyo " +
                    "periodo se solapa con la fecha de hoy. La vista previa dice quiénes."));

            // Cinturón: la misma comprobación por el repositorio, por si el plan y DEC-19 llegaran a separarse.
            foreach (var trabajadorId in porAsignar)
            {
                if (await asignaciones.ExisteSolapeAsync(trabajadorId, centroId, hoy, null, ct))
                    return Result.Fallo(Error.Crear(CodigoSolape,
                        "Un trabajador ya tuvo una asignación a este centro cuyo periodo se solapa con la fecha de hoy."));
            }

            // Todo rechazo va antes de agregar nada.
            var agregada = await agregarVisita(ct);
            if (agregada.EsFallido)
                return Result.Fallo(agregada.Error);
            visita = agregada.Valor;

            var nuevas = new List<Asignacion>();
            foreach (var trabajadorId in porAsignar)
            {
                var asignacion = new Asignacion(trabajadorId, centroId, hoy);
                asignaciones.Agregar(asignacion);
                nuevas.Add(asignacion);
            }

            if (nuevas.Count > 0)
                await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Asignaciones = nuevas }, ct);

            foreach (var gestion in resultado.Gestiones)
                gestiones.Agregar(new Gestion(gestion.TrabajadorId, centroId, gestion.TipoDocumentoId, visita.MensajeOrigenId));

            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);

        return guardado.EsExitoso
            ? Result.Exito((visita, resultado))
            : Result.Fallo<(VisitaAgregada, PlanAltaVisitaDto)>(guardado.Error);
    }
}
