using CaeManager.Domain.Common;
using CaeManager.Domain.RelacionesEmpresariales;

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

    /// <summary>Mismo límite que la «Nota interna» de Empresa 360 (<c>Empresa.LongitudMaximaNotas</c>).</summary>
    public const int LongitudMaximaNotas = 2000;

    /// <summary>
    /// La «Nota interna» de la ficha Centro 360: texto libre del equipo sobre el Centro. NULL = sin nota. Solo la
    /// escribe <see cref="FijarNotaInterna"/>; <see cref="Actualizar"/> y <see cref="ActualizarFicha"/> no la tocan.
    /// </summary>
    public string? Notas { get; private set; }

    /// <summary>
    /// Ancla del papel por Centro (DECISION_LOG, 2026-11-08, «Papel por Centro»): la Relación Empresarial
    /// <c>EmpresaId → X</c> por la que la Empresa del Centro entra en él como subcontrata de X, la contratista
    /// principal. <see cref="ClienteId"/> sigue siendo el titular del Centro. NULL = entra como contratista
    /// principal (o no se ha dicho) y no se muestra nada. El papel se calcula a partir de esta arista y no se
    /// guarda (ADR-011): no hay columna de rol. Solo la escriben <see cref="AnclarRelacionDeEntrada"/> y
    /// <see cref="QuitarAnclaDeEntrada"/>; el alta, la edición, la ficha, la importación y la restauración no la tocan.
    /// Quitarla renueva la versión del Centro aunque no hubiera ancla (ver <see cref="QuitarAnclaDeEntrada"/>).
    /// </summary>
    public Guid? RelacionEmpresarialEntradaId { get; private set; }

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

    /// <summary>
    /// Fija solo la nota interna (<see cref="Notas"/>), sin tocar nombre, dirección ni ficha: quien edita la nota
    /// desde la ficha 360 no reenvía los datos del Centro. Vacía o solo espacios equivale a no tener nota
    /// (<c>null</c>).
    /// </summary>
    public void FijarNotaInterna(string? notas)
    {
        var normalizada = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();

        if (normalizada is { Length: > LongitudMaximaNotas })
            throw new ArgumentException($"La nota interna no puede superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = normalizada;
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

    /// <summary>
    /// Fija la Relación Empresarial por la que la Empresa del Centro entra como subcontrata. La arista tiene que
    /// ser del mismo Tenant propietario, estar vigente, tener como proveedora la Empresa del Centro y como cliente
    /// una Empresa distinta del titular: si el cliente fuera el titular, la Empresa entraría como contratista
    /// principal, y entonces no hay ancla. La FK compuesta con <c>TenantId</c> repite en la base la primera
    /// condición; las demás solo se comprueban aquí.
    /// </summary>
    public Result AnclarRelacionDeEntrada(RelacionEmpresarial relacion)
    {
        ArgumentNullException.ThrowIfNull(relacion);

        // La de otro Tenant responde como inexistente: no se confirma que exista.
        if (relacion.TenantId != TenantId)
            return Result.Fallo(Error.Crear(
                "Centro.RelacionEntradaNoEncontrada", "No encontramos esa relación empresarial."));
        if (!relacion.EstaVigente)
            return Result.Fallo(Error.Crear(
                "Centro.RelacionEntradaNoVigente", "Esa relación empresarial ya no está vigente."));
        if (relacion.ProveedoraId != EmpresaId)
            return Result.Fallo(Error.Crear(
                "Centro.RelacionEntradaDeOtraEmpresa", "Esa relación empresarial no es de la Empresa de este centro."));
        if (relacion.ClienteId == ClienteId)
            return Result.Fallo(Error.Crear(
                "Centro.RelacionEntradaConElTitular",
                "Con el titular del centro la Empresa entra como contratista principal: no hace falta indicar nada."));

        RelacionEmpresarialEntradaId = relacion.Id;
        return Result.Exito();
    }

    /// <summary>
    /// La Empresa del Centro vuelve a entrar como contratista principal: no se muestra nada. Renueva la versión
    /// siempre, también si no había ancla: así quitar un ancla cuya arista se cerró (que se permite sin ver a su X,
    /// DECISION_LOG, 2026-11-11, punto 11 b) y quitar en un Centro sin ancla tienen el mismo efecto observable
    /// —un UPDATE del Centro y una versión nueva— y la versión de la ficha no revela si había un ancla cerrada.
    /// </summary>
    public void QuitarAnclaDeEntrada()
    {
        RelacionEmpresarialEntradaId = null;
        RenovarVersion();
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
