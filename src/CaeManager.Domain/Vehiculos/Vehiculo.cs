using CaeManager.Domain.Common;

namespace CaeManager.Domain.Vehiculos;

/// <summary>
/// Vehículo de una Empresa o de una Subcontrata (nunca ambas — ver
/// <see cref="DeEmpresa"/>/<see cref="DeSubcontrata"/>), con su
/// documentación asociada (ITC, ficha técnica, seguro, autorización de
/// circulación — ver Documento.DeVehiculo). Mismo patrón que Trabajador:
/// un vehículo también necesita su documentación en regla antes de entrar
/// a un Centro, y puede incluirse en una Visita igual que un Trabajador.
/// </summary>
public class Vehiculo : EntidadBase
{
    public const int LongitudMaximaNombre = 100;
    public const int LongitudMaximaModelo = 100;
    public const int LongitudMaximaNumeroPlaca = 20;
    public const int LongitudMaximaNotas = 2000;

    public Guid? EmpresaId { get; private set; }
    public Guid? SubcontrataId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string Modelo { get; private set; } = string.Empty;
    public string NumeroPlaca { get; private set; } = string.Empty;

    /// <summary>
    /// La «Nota interna» de la ficha Vehículo 360: texto libre del equipo
    /// sobre el vehículo. NULL = sin nota. Solo la escribe
    /// <see cref="FijarNotaInterna"/>; <see cref="Actualizar"/> no la toca.
    /// </summary>
    public string? Notas { get; private set; }

    public bool EsDeSubcontrata => SubcontrataId is not null;

    private Vehiculo()
    {
    }

    private Vehiculo(Guid? empresaId, Guid? subcontrataId, string nombre, string modelo, string numeroPlaca)
    {
        EmpresaId = empresaId;
        SubcontrataId = subcontrataId;
        EstablecerNombre(nombre);
        EstablecerModelo(modelo);
        EstablecerNumeroPlaca(numeroPlaca);
    }

    public static Vehiculo DeEmpresa(Guid empresaId, string nombre, string modelo, string numeroPlaca)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El vehículo debe pertenecer a una empresa.", nameof(empresaId));

        return new Vehiculo(empresaId, null, nombre, modelo, numeroPlaca);
    }

    public static Vehiculo DeSubcontrata(Guid subcontrataId, string nombre, string modelo, string numeroPlaca)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("El vehículo debe pertenecer a una subcontrata.", nameof(subcontrataId));

        return new Vehiculo(null, subcontrataId, nombre, modelo, numeroPlaca);
    }

    public void Actualizar(string nombre, string modelo, string numeroPlaca)
    {
        EstablecerNombre(nombre);
        EstablecerModelo(modelo);
        EstablecerNumeroPlaca(numeroPlaca);
    }

    /// <summary>
    /// Fija solo la nota interna (<see cref="Notas"/>), sin tocar nombre,
    /// modelo ni matrícula: quien edita la nota desde la ficha 360 no reenvía
    /// los datos del vehículo. Vacía o solo espacios equivale a no tener nota
    /// (<c>null</c>).
    /// </summary>
    public void FijarNotaInterna(string? notas)
    {
        var normalizada = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();

        if (normalizada is { Length: > LongitudMaximaNotas })
            throw new ArgumentException($"La nota interna no puede superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = normalizada;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del vehículo es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }

    private void EstablecerModelo(string modelo)
    {
        if (string.IsNullOrWhiteSpace(modelo))
            throw new ArgumentException("El modelo del vehículo es obligatorio.", nameof(modelo));

        var normalizado = modelo.Trim();

        if (normalizado.Length > LongitudMaximaModelo)
            throw new ArgumentException($"El modelo no puede superar {LongitudMaximaModelo} caracteres.", nameof(modelo));

        Modelo = normalizado;
    }

    private void EstablecerNumeroPlaca(string numeroPlaca)
    {
        if (string.IsNullOrWhiteSpace(numeroPlaca))
            throw new ArgumentException("La matrícula del vehículo es obligatoria.", nameof(numeroPlaca));

        var normalizada = numeroPlaca.Trim().ToUpperInvariant();

        if (normalizada.Length > LongitudMaximaNumeroPlaca)
            throw new ArgumentException($"La matrícula no puede superar {LongitudMaximaNumeroPlaca} caracteres.", nameof(numeroPlaca));

        NumeroPlaca = normalizada;
    }
}
