using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.RelacionesEmpresariales;

/// <summary>
/// <b>Una arista viva no se cierra mientras siga sosteniendo operación real.</b>
/// (PD-1: sin cascada destructiva — se bloquea, no se arrastra.)
/// </summary>
/// <remarks>
/// <para>
/// <b>Por qué hay DOS condiciones y no una.</b> El diseño de F5 § 5.4(a)
/// formulaba el guard con una sola: <i>«se bloquea si existe un Centro vivo
/// con <c>ClienteId = C</c> y <c>EmpresaId = P</c>»</i>. Eso es correcto para
/// la arista ordinaria (contratista → titular), y <b>estructuralmente ciego</b>
/// para la arista de PD-4 (subcontrata → contratista): en <c>Centros</c>,
/// <c>ClienteId</c> es el titular y <c>EmpresaId</c> la contratista, así que
/// para el par (subcontrata, contratista) <b>esa fila no existe ni puede
/// existir</b>. La única defensa que el documento diseñaba no cubría el caso
/// que motivó la reapertura, y romperlo era un clic: abrir la ficha de la
/// subcontrata, desmarcar la contratista, guardar.
/// </para>
/// <para>
/// La dependencia de esa segunda arista no vive en <c>Centros</c> sino en
/// <c>Asignaciones</c>: los trabajadores de la subcontrata asignados a
/// centros de la contratista. De ahí la segunda condición.
/// </para>
/// <para>
/// <b>Se evalúa sobre las BAJAS calculadas, nunca sobre el conjunto de
/// contrapartes</b> (§ 5.4(b), invariante de F4.2c): una contraparte opaca
/// —soft-deleted o no clasificable— no origina baja, y por tanto tampoco
/// puede originar bloqueo. Bloquear por algo que el usuario no pudo ver ni
/// desmarcar sería el mismo error que cerrar por ausencia, con el signo
/// cambiado.
/// </para>
/// <para>
/// <b>Aquí sí se mide con los filtros globales puestos</b>, al contrario que
/// P0 (F5-D6). No es incoherencia: P0 audita si existen violaciones —y para
/// eso hay que ver las filas muertas—, mientras que este guard pregunta si
/// hay <i>operación viva</i> que proteger. Un centro borrado no sostiene
/// nada, y desde el cierre de asignaciones por borrado sus asignaciones ya
/// no están activas.
/// </para>
/// </remarks>
public class GuardDeCierreDeArista(
    ICentrosQueryContext centros,
    IAsignacionesQueryContext asignaciones,
    ITrabajadoresQueryContext trabajadores) : IGuardDeCierreDeArista
{
    public async Task<bool> TieneOperacionVivaAsync(
        Guid proveedoraId, Guid clienteId, CancellationToken cancellationToken = default)
    {
        // (1) Arista ordinaria: la proveedora es la contratista de un centro
        //     cuyo titular es el cliente de la arista.
        var esContratistaDeUnCentroDelCliente = await centros.Centros
            .AnyAsync(c => c.ClienteId == clienteId && c.EmpresaId == proveedoraId, cancellationToken);

        if (esContratistaDeUnCentroDelCliente)
            return true;

        // (2) Arista de PD-4: la proveedora es una subcontrata con gente
        //     trabajando ahora mismo en centros de la contratista.
        var centrosDelCliente = centros.Centros
            .Where(c => c.EmpresaId == clienteId)
            .Select(c => c.Id);

        var trabajadoresDeLaProveedora = trabajadores.Trabajadores
            .Where(t => t.SubcontrataId == proveedoraId)
            .Select(t => t.Id);

        return await asignaciones.Asignaciones.AnyAsync(
            a => a.FechaBaja == null
                 && centrosDelCliente.Contains(a.CentroId)
                 && trabajadoresDeLaProveedora.Contains(a.TrabajadorId),
            cancellationToken);
    }
}
