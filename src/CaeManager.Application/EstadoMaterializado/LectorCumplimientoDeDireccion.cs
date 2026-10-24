using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Un grupo de pares exigidos de <c>EstadosRequisito</c> agregado en SQL por Empresa del Trabajador, Centro y estado en TALVEG,
/// con lo que hace falta para decidir si la tabla sirve (<see cref="Vencidas"/>, <see cref="DeOtraVersion"/>).
/// </summary>
/// <param name="Cantidad">Pares del grupo.</param>
/// <param name="Vencidas">Pares cuya <c>ProximoCambioEl</c> ya llegó: el estado guardado es de un día anterior.</param>
/// <param name="DeOtraVersion">Pares calculados con una versión de la regla que no es la vigente.</param>
public sealed record GrupoDeParesExigidos(
    Guid EmpresaId, Guid CentroId, EstadoTalvegRequisito Estado, int Cantidad, int Vencidas, int DeOtraVersion);

/// <summary>
/// Lo que el Cuadro de dirección y el Dashboard ejecutivo leen de las tablas del estado materializado (PR 10 del plan). Solo
/// el cumplimiento, que las tablas guardan igual que el cálculo de hoy; nada que sea facturación ni tarifas.
///
/// <para>
/// Cada Tenant se decide por separado: el lector corre dentro del <c>AmbitoTenantExplicito</c> del Tenant que mira quien
/// llama, con el filtro global y la RLS de ese Tenant, y no mezcla datos de otro. Lo que no devuelve, se calcula como siempre.
/// </para>
/// </summary>
public interface ILectorCumplimientoDeDireccion
{
    /// <summary>
    /// El cumplimiento de esos Centros leído de <c>EstadosEntidad</c> (el mismo <c>FraccionCumplimiento</c> que
    /// <c>ICalculoEstadoCentroService.CalcularCumplimientoAsync</c>), con las guardas de <see cref="ILectorEstadoCentros"/>:
    /// Tenant completo y fila fiable. Los Centros que no salen en el resultado se calculan.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CumplimientoDeCentrosAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken);

    /// <summary>
    /// El cumplimiento en contexto Empresa de las Empresas propias del Tenant (la columna por Tenant beneficiario del Cuadro de
    /// dirección), leído de <c>EstadosRequisito</c>: la fracción de los pares Trabajador × Tipo que exigen los Centros donde
    /// sus Trabajadores tienen Asignación activa. <c>null</c> si la tabla no puede decidirlo y hay que calcular.
    /// </summary>
    /// <param name="centrosPorEmpresa">Los pares (Empresa propia, Centro) de sus Asignaciones activas, como los saca la consulta de siempre.</param>
    Task<FraccionCumplimiento?> CumplimientoExigidoAsync(
        IReadOnlySet<(Guid EmpresaId, Guid CentroId)> centrosPorEmpresa, CancellationToken cancellationToken);
}

/// <summary>El lector que nunca lee: todo se calcula. Para los contenedores que no registran las tablas del estado.</summary>
public sealed class SinLectorDeCumplimientoDeDireccion : ILectorCumplimientoDeDireccion
{
    public static SinLectorDeCumplimientoDeDireccion Instancia { get; } = new();

    private SinLectorDeCumplimientoDeDireccion()
    {
    }

    public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CumplimientoDeCentrosAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, FraccionCumplimiento>>(new Dictionary<Guid, FraccionCumplimiento>());

    public Task<FraccionCumplimiento?> CumplimientoExigidoAsync(
        IReadOnlySet<(Guid EmpresaId, Guid CentroId)> centrosPorEmpresa, CancellationToken cancellationToken) =>
        Task.FromResult<FraccionCumplimiento?>(null);
}

/// <inheritdoc cref="ILectorCumplimientoDeDireccion"/>
public sealed class LectorCumplimientoDeDireccion(
    IEstadoMaterializadoQueryContext tablas,
    ILectorEstadoCentros lectorCentros,
    IAlcanceDatosService alcanceDatos,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext)
    : ILectorCumplimientoDeDireccion
{
    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CumplimientoDeCentrosAsync(
        IReadOnlyCollection<Guid> centroIds, CancellationToken cancellationToken)
    {
        // El lector de Centros ya exige el Tenant completo y la fila fiable, y no devuelve nada si no se cumple.
        var leidos = await lectorCentros.LeerAsync(centroIds, cancellationToken);
        return leidos.ToDictionary(l => l.Key, l => l.Value.Cumplimiento);
    }

    public async Task<FraccionCumplimiento?> CumplimientoExigidoAsync(
        IReadOnlySet<(Guid EmpresaId, Guid CentroId)> centrosPorEmpresa, CancellationToken cancellationToken)
    {
        if (centrosPorEmpresa.Count == 0)
            return null;

        // Quien no alcanza el Tenant entero no ve este agregado y nunca llega aquí; se comprueba igualmente: la tabla guarda
        // el estado de todos los Centros y no se recorta a una cartera parcial.
        if (!await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken))
            return null;

        if (!await lectorCentros.TenantCompletoAsync(cancellationToken))
            return null;

        var centroIds = centrosPorEmpresa.Select(c => c.CentroId).Distinct().ToList();
        var empresaIds = centrosPorEmpresa.Select(c => c.EmpresaId).Distinct().ToList();

        // Un Centro con Asignaciones activas y sin fila propia es un hueco que cubrirá el cotejo (diseño § 13.7): se calcula.
        var centrosConFila = await tablas.EstadosEntidad
            .CountAsync(e => e.Ambito == AmbitoEstadoEntidad.Centro && centroIds.Contains(e.EntidadId), cancellationToken);
        if (centrosConFila != centroIds.Count)
            return null;

        var hoy = DiaDeNegocio.Hoy();
        const int versionDeLaRegla = CalculoEstadoMaterializado.VersionDeLaRegla;

        // Una sentencia: los pares Trabajador × Tipo de esos Centros, de Trabajadores de las Empresas propias, por Empresa,
        // Centro y estado. Las Empresas y los Trabajadores salen ya con el filtro global (los eliminados no cuentan).
        var pares = tablas.EstadosRequisito
            .Where(p => p.AmbitoTitular == AmbitoAplicacion.Trabajador && centroIds.Contains(p.CentroId));
        var grupos = (await (
                from p in pares
                join t in trabajadoresContext.Trabajadores on p.TitularId equals t.Id
                where t.EmpresaId != null && empresaIds.Contains(t.EmpresaId.Value)
                group p by new { EmpresaId = t.EmpresaId!.Value, p.CentroId, p.EstadoTalveg } into g
                select new
                {
                    g.Key.EmpresaId,
                    g.Key.CentroId,
                    g.Key.EstadoTalveg,
                    Cantidad = g.Count(),
                    Vencidas = g.Count(x => x.ProximoCambioEl != null && x.ProximoCambioEl <= hoy),
                    DeOtraVersion = g.Count(x => x.VersionCalculo != versionDeLaRegla),
                }).ToListAsync(cancellationToken))
            .Select(g => new GrupoDeParesExigidos(g.EmpresaId, g.CentroId, g.EstadoTalveg, g.Cantidad, g.Vencidas, g.DeOtraVersion))
            .ToList();

        // La renuncia no se guarda en el par: está en el documento efectivo (Documento.EsRenuncia) y nunca es conforme. El
        // documento efectivo es siempre un operativo (nunca un histórico sustituido).
        var renuncias = (await (
                from p in pares
                join t in trabajadoresContext.Trabajadores on p.TitularId equals t.Id
                join d in documentosContext.Documentos.Operativos() on p.DocumentoEfectivoId equals (Guid?)d.Id
                where d.EsRenuncia && t.EmpresaId != null && empresaIds.Contains(t.EmpresaId.Value)
                group p by new { EmpresaId = t.EmpresaId!.Value, p.CentroId, p.EstadoTalveg } into g
                select new { g.Key.EmpresaId, g.Key.CentroId, g.Key.EstadoTalveg, Cantidad = g.Count() })
                .ToListAsync(cancellationToken))
            .Select(g => new GrupoDeParesExigidos(g.EmpresaId, g.CentroId, g.EstadoTalveg, g.Cantidad, 0, 0))
            .ToList();

        // Sin ningún par guardado para Centros donde hay Asignaciones activas no se sabe si no se exige nada o si faltan filas
        // (un hueco del detector, diseño § 13.7): lo que sí se sabe es calcular.
        if (grupos.Count == 0)
            return null;

        return Sumar(grupos, renuncias, centrosPorEmpresa);
    }

    /// <summary>
    /// La fracción de los grupos que pertenecen a las (Empresa, Centro) dadas, con la misma regla de «al día» que el cálculo
    /// (<see cref="CumplimientoDocumental.EsConforme(EstadoDocumento, bool)"/>): todo par entra en el denominador y solo el que
    /// vale y no está en renuncia, en el numerador. Pura. <c>null</c> si algún grupo no se puede dar por bueno: una frontera ya
    /// llegada, otra versión de la regla o un estado que no es el de un documento.
    /// </summary>
    public static FraccionCumplimiento? Sumar(
        IReadOnlyCollection<GrupoDeParesExigidos> grupos,
        IReadOnlyCollection<GrupoDeParesExigidos> renuncias,
        IReadOnlySet<(Guid EmpresaId, Guid CentroId)> centrosPorEmpresa)
    {
        var renunciasPorGrupo = renuncias
            .GroupBy(r => (r.EmpresaId, r.CentroId, r.Estado))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Cantidad));

        var alDia = 0;
        var requeridos = 0;
        foreach (var grupo in grupos.Where(g => centrosPorEmpresa.Contains((g.EmpresaId, g.CentroId))))
        {
            if (grupo.Vencidas > 0 || grupo.DeOtraVersion > 0 || grupo.Estado.ComoEstadoDocumento() is not { } estado)
                return null;

            var enRenuncia = Math.Min(grupo.Cantidad, renunciasPorGrupo.GetValueOrDefault((grupo.EmpresaId, grupo.CentroId, grupo.Estado)));
            requeridos += grupo.Cantidad;
            if (CumplimientoDocumental.EsConforme(estado, esRenuncia: false))
                alDia += grupo.Cantidad - enRenuncia;
            if (CumplimientoDocumental.EsConforme(estado, esRenuncia: true))
                alDia += enRenuncia;
        }

        return new FraccionCumplimiento(alDia, requeridos);
    }
}
