using CaeManager.Domain.Common;

namespace CaeManager.Domain.Centros;

/// <summary>
/// Ubicación física de un Cliente donde trabajan nuestros trabajadores.
/// Normaliza el problema del Excel original, donde varios centros de un
/// mismo cliente aparecían fusionados como texto libre en una sola fila
/// (ver Project-Hydra-Negocio/tecnico/DATABASE.md).
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

    /// <summary>
    /// Si el Centro exige documentación CAE. Por defecto sí; ver
    /// <see cref="ModalidadGestionCae"/>.
    /// </summary>
    public ModalidadGestionCae GestionCae { get; private set; } = ModalidadGestionCae.ConGestionCae;

    public bool RequiereGestionCae => GestionCae == ModalidadGestionCae.ConGestionCae;

    public string? HorarioAcceso { get; private set; }
    public string? ControlAccesosNombre { get; private set; }
    public string? ControlAccesosTelefono { get; private set; }
    public string? TecnicoPrevencionNombre { get; private set; }
    public string? TecnicoPrevencionTelefono { get; private set; }
    public string? TecnicoPrevencionCorreo { get; private set; }
    public bool? TrabajosEnAltura { get; private set; }
    public bool? EspaciosConfinados { get; private set; }
    public bool? AtmosferasExplosivas { get; private set; }

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

    public void EstablecerGestionCae(ModalidadGestionCae modalidad)
    {
        if (!Enum.IsDefined(modalidad))
            throw new ArgumentOutOfRangeException(nameof(modalidad), modalidad, "Modalidad de gestión CAE desconocida.");

        GestionCae = modalidad;
    }

    public void ActualizarFicha(
        string? horarioAcceso,
        string? controlAccesosNombre,
        string? controlAccesosTelefono,
        string? tecnicoPrevencionNombre,
        string? tecnicoPrevencionTelefono,
        string? tecnicoPrevencionCorreo,
        bool? trabajosEnAltura,
        bool? espaciosConfinados,
        bool? atmosferasExplosivas)
    {
        var valorHorarioAcceso = NormalizarCampoFicha(horarioAcceso, 500, nameof(horarioAcceso));
        var valorControlAccesosNombre = NormalizarCampoFicha(controlAccesosNombre, 200, nameof(controlAccesosNombre));
        var valorControlAccesosTelefono = NormalizarCampoFicha(controlAccesosTelefono, 50, nameof(controlAccesosTelefono));
        var valorTecnicoPrevencionNombre = NormalizarCampoFicha(tecnicoPrevencionNombre, 200, nameof(tecnicoPrevencionNombre));
        var valorTecnicoPrevencionTelefono = NormalizarCampoFicha(tecnicoPrevencionTelefono, 50, nameof(tecnicoPrevencionTelefono));
        var valorTecnicoPrevencionCorreo = NormalizarCampoFicha(tecnicoPrevencionCorreo, 254, nameof(tecnicoPrevencionCorreo));
        HorarioAcceso = valorHorarioAcceso;
        ControlAccesosNombre = valorControlAccesosNombre;
        ControlAccesosTelefono = valorControlAccesosTelefono;
        TecnicoPrevencionNombre = valorTecnicoPrevencionNombre;
        TecnicoPrevencionTelefono = valorTecnicoPrevencionTelefono;
        TecnicoPrevencionCorreo = valorTecnicoPrevencionCorreo;
        TrabajosEnAltura = trabajosEnAltura;
        EspaciosConfinados = espaciosConfinados;
        AtmosferasExplosivas = atmosferasExplosivas;
    }

    private static string? NormalizarCampoFicha(string? valor, int longitudMaxima, string parametro)
    {
        var normalizado = string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        if (normalizado?.Length > longitudMaxima)
            throw new ArgumentException($"El campo no puede superar {longitudMaxima} caracteres.", parametro);
        return normalizado;
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
