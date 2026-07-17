using CaeManager.Domain.Common;

namespace CaeManager.Domain.Clientes;

/// <summary>
/// Empresa titular de uno o más Centros de Trabajo (ver DATABASE.md). No opera
/// el sistema; es el destinatario de la coordinación CAE.
/// </summary>
public class Cliente : EntidadBase
{
    public const int LongitudMaximaRazonSocial = 200;
    public const int LongitudMaximaNotas = 2000;
    public const int LongitudCif = 9;

    public string RazonSocial { get; private set; } = string.Empty;
    public string Cif { get; private set; } = string.Empty;
    public bool EsCritico { get; private set; }
    public string? Notas { get; private set; }

    /// <summary>
    /// El Gestor CAE (rol GestorCae) dueño de este Cliente — "creado o
    /// asignado" (ver Roles.cs). Null significa sin gestor asignado, visible
    /// solo para Administrador/DireccionCae hasta que se reasigne (ver
    /// pantalla Supervisión, Fase futura). Guid suelto hacia ApplicationUser
    /// (Infrastructure.Identity) — Domain no puede referenciarlo directamente,
    /// mismo patrón que EliminadoPorUsuarioId.
    /// </summary>
    public Guid? EjecutivoUsuarioId { get; private set; }

    private Cliente()
    {
        // Requerido por EF Core.
    }

    public Cliente(string razonSocial, string cif, bool esCritico, string? notas = null, Guid? ejecutivoUsuarioId = null)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
        EsCritico = esCritico;
        Notas = notas;
        EjecutivoUsuarioId = ejecutivoUsuarioId;
    }

    /// <summary>Reasigna la cartera a otro Gestor CAE (o la deja sin asignar con null).</summary>
    public void AsignarEjecutivo(Guid? ejecutivoUsuarioId) => EjecutivoUsuarioId = ejecutivoUsuarioId;

    public void Actualizar(string razonSocial, string cif, bool esCritico, string? notas)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
        EsCritico = esCritico;
        Notas = notas;
    }

    private void EstablecerRazonSocial(string razonSocial)
    {
        if (string.IsNullOrWhiteSpace(razonSocial))
            throw new ArgumentException("La razón social del cliente es obligatoria.", nameof(razonSocial));

        var normalizada = razonSocial.Trim();

        if (normalizada.Length > LongitudMaximaRazonSocial)
            throw new ArgumentException(
                $"La razón social del cliente no puede superar {LongitudMaximaRazonSocial} caracteres.", nameof(razonSocial));

        RazonSocial = normalizada;
    }

    private void EstablecerCif(string cif)
    {
        if (string.IsNullOrWhiteSpace(cif))
            throw new ArgumentException("El CIF del cliente es obligatorio.", nameof(cif));

        var normalizado = cif.Trim().ToUpperInvariant();
        var resultado = ValidadorIdentificacion.Analizar(normalizado);

        if (resultado.Tipo != TipoIdentificacion.NifEmpresa || !resultado.EsValido)
            throw new ArgumentException("El CIF no es válido.", nameof(cif));

        Cif = normalizado;
    }
}
