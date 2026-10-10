using System.Text.RegularExpressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Orden en que un usuario quiere ver las cajas de la pestaña «Ficha» de un
/// tipo de ficha 360 (decisión del 2026-10-09). Es una preferencia personal:
/// no es un dato de negocio del Tenant propietario ni cambia lo que ven otros
/// usuarios. Vale para todas las fichas de ese tipo, no para una ficha concreta.
///
/// La clave es Tenant + Usuario + Tipo de ficha, una sola fila: se aísla por
/// Tenant como <see cref="FiltroGuardado"/>, así que un Gestor CAE con
/// Asignación de Cartera sobre varios Tenants tiene un orden en cada uno.
///
/// Que no haya fila significa «orden automático» (de la caja más alta a la más
/// baja, lo decide la interfaz). «Restablecer orden» borra la fila; por eso la
/// lista nunca se guarda vacía.
///
/// El dominio valida solo la forma de las claves. Qué cajas existen en cada
/// tipo de ficha lo sabe la pantalla que las pinta, y el orden guardado se
/// concilia con ellas al leer (<see cref="Conciliar"/>): la clave guardada que
/// ya no existe se ignora y la caja nueva que no está en el orden va al final.
/// </summary>
public partial class OrdenCajasFicha : EntidadConTenant
{
    public const int LongitudMaximaTipoFicha = 50;
    public const int LongitudMaximaClave = 64;
    public const int MaximoClaves = 100;

    public Guid UsuarioId { get; private set; }
    public string TipoFicha { get; private set; } = string.Empty;
    public List<string> Claves { get; private set; } = [];
    public DateTime ActualizadoEnUtc { get; private set; }

    private OrdenCajasFicha()
    {
        // Requerido por EF Core.
    }

    public OrdenCajasFicha(Guid usuarioId, string tipoFicha, IReadOnlyList<string> claves, DateTime ahoraUtc)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El orden de cajas debe pertenecer a un usuario.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(tipoFicha) || tipoFicha.Length > LongitudMaximaTipoFicha)
            throw new ArgumentException("Falta el tipo de ficha al que pertenece el orden.", nameof(tipoFicha));

        UsuarioId = usuarioId;
        TipoFicha = tipoFicha;
        Reordenar(claves, ahoraUtc);
    }

    public void Reordenar(IReadOnlyList<string> claves, DateTime ahoraUtc)
    {
        if (ErrorDeForma(claves) is { } error)
            throw new ArgumentException(error, nameof(claves));

        Claves = [.. claves];
        ActualizadoEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Por qué esa lista no se puede guardar, o <c>null</c> si tiene buena forma:
    /// entre 1 y <see cref="MaximoClaves"/> claves, sin repetir, cada una en
    /// minúsculas con guiones y de <see cref="LongitudMaximaClave"/> caracteres como mucho.
    /// </summary>
    public static string? ErrorDeForma(IReadOnlyList<string>? claves)
    {
        if (claves is null || claves.Count == 0)
            return "El orden de cajas no puede estar vacío.";
        if (claves.Count > MaximoClaves)
            return "El orden de cajas tiene demasiadas claves.";
        if (claves.Any(c => c is null || c.Length > LongitudMaximaClave || !FormaDeClave().IsMatch(c)))
            return "El orden de cajas contiene una clave con formato no válido.";
        if (claves.Distinct(StringComparer.Ordinal).Count() != claves.Count)
            return "El orden de cajas repite una clave.";
        return null;
    }

    /// <summary>
    /// El orden en que se pintan las cajas de una ficha: primero las del orden
    /// guardado que la ficha tiene, en ese orden; después las que la ficha tiene
    /// y el orden guardado no nombra, en el orden en que llegan. Una clave
    /// guardada que la ficha no tiene se ignora. Sin orden guardado devuelve las
    /// cajas tal como llegan.
    /// </summary>
    public static IReadOnlyList<string> Conciliar(IReadOnlyList<string> ordenGuardado, IReadOnlyList<string> cajasDeLaFicha)
    {
        var presentes = cajasDeLaFicha.ToHashSet(StringComparer.Ordinal);
        var resultado = new List<string>(cajasDeLaFicha.Count);
        var puestas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var clave in ordenGuardado)
            if (presentes.Contains(clave) && puestas.Add(clave))
                resultado.Add(clave);

        foreach (var clave in cajasDeLaFicha)
            if (puestas.Add(clave))
                resultado.Add(clave);

        return resultado;
    }

    // \z y no $: en .NET, $ casa también antes de un salto de línea final.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex FormaDeClave();
}
