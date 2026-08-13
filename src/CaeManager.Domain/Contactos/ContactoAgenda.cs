using CaeManager.Domain.Common;

namespace CaeManager.Domain.Contactos;

/// <summary>
/// Persona concreta a la que se le pide algo, dentro de la agenda de un
/// Cliente, Empresa, Subcontrata o Centro.
///
/// Existe porque "el contacto del cliente" no es uno solo: en un mismo Cliente
/// el RLC/TC1 se le pide a Finanzas, los aptos médicos a Vigilancia de la
/// Salud, los EPIs al responsable de PRL y la programación de visitas a otra
/// persona distinta (requisito del usuario, 2026-08-13). Hasta ahora el
/// destinatario de una reclamación salía de los usuarios de portal del Cliente
/// (<c>IContactosClienteService.ObtenerEmailsPortalAsync</c>), que es "quién
/// tiene cuenta", no "a quién le toca esto".
///
/// **Propietario polimórfico con FK real**: exactamente uno de
/// <see cref="ClienteId"/>/<see cref="EmpresaId"/>/<see cref="SubcontrataId"/>/
/// <see cref="CentroId"/> tiene valor, garantizado por un CHECK en base de
/// datos — mismo patrón que <c>Documento</c> (ver la migración
/// <c>RendimientoBusquedasYCheckXorDocumento</c>), no el polimorfismo sin FK de
/// <c>ParticipanteConversacion</c>: aquí sí queremos integridad referencial,
/// porque una agenda huérfana de su dueño no significa nada.
///
/// <see cref="Email"/> es obligatorio y <see cref="Telefono"/> no: la solicitud
/// de documentación viaja siempre por correo (decisión del usuario); el
/// teléfono se guarda para poder llamar o escribir por WhatsApp, no para
/// reclamar.
/// </summary>
public class ContactoAgenda : EntidadBase
{
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaEmail = 320;
    public const int LongitudMaximaCargo = 150;
    public const int LongitudMaximaTelefono = 20;
    public const int LongitudMaximaNotas = 1000;

    private readonly List<ContactoAgendaTipoDocumento> _tiposDocumento = [];

    public Guid? ClienteId { get; private set; }
    public Guid? EmpresaId { get; private set; }
    public Guid? SubcontrataId { get; private set; }
    public Guid? CentroId { get; private set; }

    public string Nombre { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string? Telefono { get; private set; }
    public string? Cargo { get; private set; }
    public string? Notas { get; private set; }

    /// <summary>
    /// Destinatario de todo lo que no tenga dueño explícito. Sin ningún
    /// predeterminado, una reclamación de un tipo de documento que nadie
    /// reclama se queda sin destinatario y hay que elegirlo a mano — que es
    /// mejor que enviárselo a quien no toca.
    /// </summary>
    public bool EsPredeterminado { get; private set; }

    /// <summary>Propósitos que no son un TipoDocumento: la programación de visitas y la facturación no se "reclaman" como documentos.</summary>
    public bool RecibeProgramacionVisitas { get; private set; }

    public bool RecibeFacturacion { get; private set; }

    /// <summary>Tipos de documento concretos que se le piden a esta persona (RLC/TC1, Apto médico, EPIS…).</summary>
    public IReadOnlyList<ContactoAgendaTipoDocumento> TiposDocumento => _tiposDocumento.AsReadOnly();

    private ContactoAgenda()
    {
    }

    private ContactoAgenda(
        Guid? clienteId, Guid? empresaId, Guid? subcontrataId, Guid? centroId,
        string nombre, string email, string? telefono, string? cargo, string? notas,
        bool esPredeterminado, bool recibeProgramacionVisitas, bool recibeFacturacion)
    {
        ClienteId = clienteId;
        EmpresaId = empresaId;
        SubcontrataId = subcontrataId;
        CentroId = centroId;

        EstablecerNombre(nombre);
        EstablecerEmail(email);
        EstablecerTelefono(telefono);
        EstablecerCargo(cargo);
        EstablecerNotas(notas);

        EsPredeterminado = esPredeterminado;
        RecibeProgramacionVisitas = recibeProgramacionVisitas;
        RecibeFacturacion = recibeFacturacion;
    }

    public static ContactoAgenda DeCliente(
        Guid clienteId, string nombre, string email, string? telefono = null, string? cargo = null, string? notas = null,
        bool esPredeterminado = false, bool recibeProgramacionVisitas = false, bool recibeFacturacion = false)
    {
        RequerirPropietario(clienteId, nameof(clienteId));
        return new ContactoAgenda(clienteId, null, null, null, nombre, email, telefono, cargo, notas,
            esPredeterminado, recibeProgramacionVisitas, recibeFacturacion);
    }

    public static ContactoAgenda DeEmpresa(
        Guid empresaId, string nombre, string email, string? telefono = null, string? cargo = null, string? notas = null,
        bool esPredeterminado = false, bool recibeProgramacionVisitas = false, bool recibeFacturacion = false)
    {
        RequerirPropietario(empresaId, nameof(empresaId));
        return new ContactoAgenda(null, empresaId, null, null, nombre, email, telefono, cargo, notas,
            esPredeterminado, recibeProgramacionVisitas, recibeFacturacion);
    }

    public static ContactoAgenda DeSubcontrata(
        Guid subcontrataId, string nombre, string email, string? telefono = null, string? cargo = null, string? notas = null,
        bool esPredeterminado = false, bool recibeProgramacionVisitas = false, bool recibeFacturacion = false)
    {
        RequerirPropietario(subcontrataId, nameof(subcontrataId));
        return new ContactoAgenda(null, null, subcontrataId, null, nombre, email, telefono, cargo, notas,
            esPredeterminado, recibeProgramacionVisitas, recibeFacturacion);
    }

    public static ContactoAgenda DeCentro(
        Guid centroId, string nombre, string email, string? telefono = null, string? cargo = null, string? notas = null,
        bool esPredeterminado = false, bool recibeProgramacionVisitas = false, bool recibeFacturacion = false)
    {
        RequerirPropietario(centroId, nameof(centroId));
        return new ContactoAgenda(null, null, null, centroId, nombre, email, telefono, cargo, notas,
            esPredeterminado, recibeProgramacionVisitas, recibeFacturacion);
    }

    public void Actualizar(
        string nombre, string email, string? telefono, string? cargo, string? notas,
        bool esPredeterminado, bool recibeProgramacionVisitas, bool recibeFacturacion)
    {
        EstablecerNombre(nombre);
        EstablecerEmail(email);
        EstablecerTelefono(telefono);
        EstablecerCargo(cargo);
        EstablecerNotas(notas);

        EsPredeterminado = esPredeterminado;
        RecibeProgramacionVisitas = recibeProgramacionVisitas;
        RecibeFacturacion = recibeFacturacion;
    }

    /// <summary>
    /// Reemplaza la lista completa de tipos de documento — no hay "añadir uno
    /// suelto" a propósito: la pantalla edita el conjunto entero con casillas,
    /// y así no hay forma de que quede a medias entre dos operaciones.
    /// </summary>
    public void EstablecerTiposDocumento(IEnumerable<Guid> tipoDocumentoIds)
    {
        _tiposDocumento.Clear();

        foreach (var tipoDocumentoId in tipoDocumentoIds.Distinct())
        {
            if (tipoDocumentoId == Guid.Empty)
                throw new ArgumentException("Un tipo de documento del contacto no puede estar vacío.", nameof(tipoDocumentoIds));

            _tiposDocumento.Add(new ContactoAgendaTipoDocumento(Id, tipoDocumentoId));
        }
    }

    private static void RequerirPropietario(Guid propietarioId, string nombreParametro)
    {
        if (propietarioId == Guid.Empty)
            throw new ArgumentException("El contacto debe pertenecer a una entidad.", nombreParametro);
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del contacto es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();
        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException($"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }

    private void EstablecerEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("El email del contacto es obligatorio — la solicitud se envía siempre por correo.", nameof(email));

        var normalizado = email.Trim();
        if (normalizado.Length > LongitudMaximaEmail)
            throw new ArgumentException($"El email no puede superar {LongitudMaximaEmail} caracteres.", nameof(email));

        // Validación deliberadamente mínima (hay un "@" con algo a cada lado):
        // la validación de formato de verdad vive en el Validator de
        // FluentValidation del Command, igual que en el resto del repositorio.
        var arroba = normalizado.IndexOf('@');
        if (arroba <= 0 || arroba == normalizado.Length - 1)
            throw new ArgumentException("El email del contacto no es válido.", nameof(email));

        Email = normalizado;
    }

    /// <summary>Mismo criterio de normalización que <c>Trabajador.Telefono</c>.</summary>
    private void EstablecerTelefono(string? telefono)
    {
        if (string.IsNullOrWhiteSpace(telefono))
        {
            Telefono = null;
            return;
        }

        var normalizado = new string(telefono.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '(' or ')' or '.')).ToArray());

        if (normalizado.StartsWith("00", StringComparison.Ordinal))
            normalizado = "+" + normalizado[2..];

        if (normalizado.Length > LongitudMaximaTelefono)
            throw new ArgumentException($"El teléfono no puede superar {LongitudMaximaTelefono} caracteres.", nameof(telefono));

        Telefono = normalizado;
    }

    private void EstablecerCargo(string? cargo)
    {
        if (cargo is not null && cargo.Length > LongitudMaximaCargo)
            throw new ArgumentException($"El cargo no puede superar {LongitudMaximaCargo} caracteres.", nameof(cargo));

        Cargo = string.IsNullOrWhiteSpace(cargo) ? null : cargo.Trim();
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas is not null && notas.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = string.IsNullOrWhiteSpace(notas) ? null : notas.Trim();
    }
}
