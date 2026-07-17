using CaeManager.Domain.Common;

namespace CaeManager.Domain.Centros;

/// <summary>
/// Ubicación física de un Cliente donde trabajan nuestros trabajadores.
/// Normaliza el problema del Excel original, donde varios centros de un
/// mismo cliente aparecían fusionados como texto libre en una sola fila
/// (ver DATABASE.md).
/// </summary>
public class Centro : EntidadBase
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaCodigo = 50;
    public const int LongitudMaximaDireccion = 300;
    public const int LongitudMaximaContacto = 500;

    public Guid ClienteId { get; private set; }
    public Guid EmpresaId { get; private set; }
    public string Nombre { get; private set; } = string.Empty;
    public string? CodigoCentro { get; private set; }
    public string? Direccion { get; private set; }
    public string? Contacto { get; private set; }
    public DateOnly? ContratoVigenteHasta { get; private set; }

    private Centro()
    {
    }

    public Centro(
        Guid clienteId,
        Guid empresaId,
        string nombre,
        string? codigoCentro = null,
        string? direccion = null,
        string? contacto = null,
        DateOnly? contratoVigenteHasta = null)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El centro debe pertenecer a un cliente.", nameof(clienteId));
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El centro debe pertenecer a una empresa.", nameof(empresaId));

        ClienteId = clienteId;
        EmpresaId = empresaId;
        EstablecerNombre(nombre);
        CodigoCentro = codigoCentro;
        Direccion = direccion;
        Contacto = contacto;
        ContratoVigenteHasta = contratoVigenteHasta;
    }

    public void Actualizar(
        string nombre,
        string? codigoCentro,
        string? direccion,
        string? contacto,
        DateOnly? contratoVigenteHasta)
    {
        EstablecerNombre(nombre);
        CodigoCentro = codigoCentro;
        Direccion = direccion;
        Contacto = contacto;
        ContratoVigenteHasta = contratoVigenteHasta;
    }

    public bool ContratoCaducado(DateOnly hoy) => ContratoVigenteHasta is not null && ContratoVigenteHasta < hoy;

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del centro es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException(
                $"El nombre del centro no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
