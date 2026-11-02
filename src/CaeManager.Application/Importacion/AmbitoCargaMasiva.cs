using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Importacion;

/// <summary>
/// Ámbito efectivo del acto que ejecuta una carga masiva, aplicado fila a fila. Cartera ≠ aislamiento de Tenant ≠
/// privilegio de plataforma: esto solo recorta lo que el Coordinador CAE (o la Dirección CAE, si su alcance no es
/// total) puede escribir dentro del Tenant actual; el aislamiento lo sigue garantizando RLS.
///
/// <para>
/// <see cref="AutoridadCargaMasiva.Total"/> (acto de Administrador) no consulta el alcance: no recorta nada. En la
/// autoridad acotada se usan las mismas listas que los comandos unitarios (de gestión para Empresas y Centros, de visibilidad para Clientes y Trabajadores, que no tienen variante de gestión: CrearTrabajador, CrearCentro,
/// CrearAsignacion…), de modo que una carga masiva nunca escribe lo que el comando unitario del mismo usuario
/// rechazaría; <c>null</c> en una lista significa «sin restricción», como en <see cref="IAlcanceDatosService"/>.
/// </para>
///
/// <para>
/// Lo que el propio fichero crea NO queda dentro del ámbito por haberlo creado (D-7: crear un Cliente empresarial no
/// concede Asignación de Cartera), salvo los Trabajadores, que solo se crean si su Empresa ya estaba dentro.
/// </para>
/// </summary>
public sealed class AmbitoCargaMasiva
{
    private readonly bool _sinRestriccion;
    private readonly HashSet<Guid>? _clientes;
    private readonly HashSet<Guid>? _empresas;
    private readonly HashSet<Guid>? _centros;
    private readonly HashSet<Guid>? _trabajadores;
    private readonly HashSet<Guid> _trabajadoresCreados = [];

    private AmbitoCargaMasiva(
        bool sinRestriccion, IReadOnlyList<Guid>? clientes, IReadOnlyList<Guid>? empresas,
        IReadOnlyList<Guid>? centros, IReadOnlyList<Guid>? trabajadores)
    {
        _sinRestriccion = sinRestriccion;
        _clientes = clientes is null ? null : [.. clientes];
        _empresas = empresas is null ? null : [.. empresas];
        _centros = centros is null ? null : [.. centros];
        _trabajadores = trabajadores is null ? null : [.. trabajadores];
    }

    public static async Task<AmbitoCargaMasiva> ResolverAsync(
        AutoridadCargaMasiva autoridad, IAlcanceDatosService alcance, CancellationToken cancellationToken)
    {
        if (autoridad == AutoridadCargaMasiva.Total)
            return new AmbitoCargaMasiva(true, null, null, null, null);

        // Fallo cerrado: sin autoridad no hay ámbito (el handler ya habrá devuelto el error antes).
        if (autoridad != AutoridadCargaMasiva.Acotada)
            return new AmbitoCargaMasiva(false, [], [], [], []);

        return new AmbitoCargaMasiva(
            false,
            await alcance.ObtenerClienteIdsVisiblesAsync(cancellationToken),
            await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken),
            await alcance.ObtenerCentroIdsParaGestionAsync(cancellationToken),
            await alcance.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken));
    }

    public bool Cliente(Guid id) => _sinRestriccion || _clientes is null || _clientes.Contains(id);

    public bool Empresa(Guid id) => _sinRestriccion || _empresas is null || _empresas.Contains(id);

    public bool Centro(Guid id) => _sinRestriccion || _centros is null || _centros.Contains(id);

    public bool Trabajador(Guid id) =>
        _sinRestriccion || _trabajadores is null || _trabajadores.Contains(id) || _trabajadoresCreados.Contains(id);

    /// <summary>Un Trabajador creado por esta carga (solo se crea con su Empresa dentro del ámbito) está dentro de él.</summary>
    public void RegistrarTrabajadorCreado(Guid id) => _trabajadoresCreados.Add(id);

    /// <summary>Motivo uniforme con el que se omite y se cuenta una fila fuera del ámbito; nunca nombra qué hay fuera.</summary>
    public static string Motivo(string quePide) =>
        $"{quePide} queda fuera de tu ámbito (tu Asignación de Cartera): la fila no se ha escrito. Pídele la carga a quien tenga ese ámbito.";

    /// <summary>
    /// Motivo de una fila cuyo DNI está en la Papelera. La Papelera no tiene ámbito decidible (el eliminado no está en
    /// ninguna lista visible), así que con ámbito acotado se da el mismo motivo que fuera de ámbito: «restáuralo» revelaría
    /// a un Coordinador CAE que ese DNI existe en el Tenant aunque no sea de su cartera.
    /// </summary>
    public string MotivoPapelera(string dni) =>
        _sinRestriccion ? DnisEnPapelera.Motivo(dni) : Motivo("Un trabajador con ese DNI");
}

/// <summary>Errores de autoridad compartidos por los comandos de carga masiva del Importador.</summary>
public static class ErroresCargaMasiva
{
    public static readonly Error SinAutoridad = Error.Crear(
        "Importacion.SinAutoridadCargaMasiva",
        "Solo el Administrador, la Dirección CAE o el Coordinador CAE pueden ejecutar una carga masiva.");
}
