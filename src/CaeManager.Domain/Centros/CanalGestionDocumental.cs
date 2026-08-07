using CaeManager.Domain.Common;

namespace CaeManager.Domain.Centros;

/// <summary>Cómo se acredita/entrega la documentación exigida por un Centro.</summary>
public enum TipoCanalGestion
{
    /// <summary>Portal externo de terceros que el Centro exige usar (p. ej. CTAIMA CAE) — requiere usuario/contraseña.</summary>
    Plataforma,

    /// <summary>Sin portal — la documentación se envía por correo a un contacto del Centro.</summary>
    Email
}

/// <summary>
/// Cómo un Centro exige acreditar/recibir su documentación CAE — o bien un
/// portal externo con login (<see cref="TipoCanalGestion.Plataforma"/>, el
/// caso original de esta entidad, antes llamada <c>PlataformaAcceso</c>), o
/// bien gestión por correo a un contacto concreto
/// (<see cref="TipoCanalGestion.Email"/>, sin portal ni credenciales) — visto
/// al diseñar el importador masivo de documentos (ver ROADMAP.md, Épico
/// "Migración masiva de datos vía ZIP + DIE"): no todos los clientes que una
/// consultora gestiona usan una plataforma, algunos solo requieren enviar la
/// documentación por email.
///
/// <b>N por Centro</b> (PLAN-EJECUCION-UX.md § 0.6, Lote 0-E) — antes era 1:1.
/// El caso real que lo rompió: un mismo Centro puede exigir el mismo portal
/// con <i>credenciales distintas</i> según a quién se gestiona (trabajadores
/// de una empresa del grupo con entidad legal propia entran por el canal del
/// cliente principal, solo cambia la credencial), y también credenciales
/// separadas para "gestión del día a día" frente a otro colectivo. De ahí
/// <see cref="EtiquetaProposito"/>: texto libre, no un catálogo cerrado —
/// los propósitos son ad-hoc de cada cliente. <see cref="EsPrincipal"/> marca
/// el canal por defecto del Centro (a lo sumo uno, garantizado además por un
/// índice único filtrado en Infrastructure); es el que se usa cuando algo
/// necesita "el" canal del Centro sin poder preguntar cuál.
///
/// <see cref="ProveedorPlataformaCaeId"/> (Lote 2-B, PLAN-EJECUCION-UX.md §
/// Parte 2 (a)) sustituye al antiguo <c>NombrePlataforma</c> de texto libre:
/// referencia el catálogo global <c>ProveedorPlataformaCae</c>, resuelto
/// automáticamente por <c>IResolucionProveedorPlataformaCaeService</c> a
/// partir de <see cref="UrlAcceso"/> — la UI solo pide selección manual
/// cuando la resolución no encuentra o encuentra más de un candidato.
///
/// Dato sensible: Usuario/Contrasena se persisten cifrados en reposo mediante
/// un ValueConverter de EF Core en Infrastructure — este tipo de dominio no
/// conoce el cifrado, solo modela el valor en texto plano en memoria durante
/// el ciclo de vida de la petición.
/// </summary>
public class CanalGestionDocumental : EntidadBase
{
    public const int LongitudMaximaUrlAcceso = 500;
    public const int LongitudMaximaEmailsDestinatarios = 500;
    public const int LongitudMaximaNombreContacto = 150;
    public const int LongitudMaximaNotas = 1000;
    public const int LongitudMaximaEtiquetaProposito = 150;

    public Guid CentroId { get; private set; }
    public TipoCanalGestion Tipo { get; private set; }

    /// <summary>
    /// Para qué sirve este canal dentro del Centro, en palabras del gestor
    /// ("Gestión general", "Trabajadores extranjeros — filial X"). Obligatorio
    /// porque con N canales por Centro es lo único que los distingue en la
    /// lista: dos accesos al mismo portal solo se diferencian por aquí.
    /// </summary>
    public string EtiquetaProposito { get; private set; } = string.Empty;

    /// <summary>Canal por defecto del Centro. A lo sumo uno por Centro.</summary>
    public bool EsPrincipal { get; private set; }

    // Solo aplica cuando Tipo == Plataforma.
    public Guid? ProveedorPlataformaCaeId { get; private set; }
    public string? UrlAcceso { get; private set; }
    public string? Usuario { get; private set; }
    public string? Contrasena { get; private set; }

    // Solo aplica cuando Tipo == Email.
    public string? EmailsDestinatarios { get; private set; }
    public string? NombreContacto { get; private set; }

    public string? Notas { get; private set; }

    private CanalGestionDocumental()
    {
    }

    private CanalGestionDocumental(Guid centroId, TipoCanalGestion tipo, string etiquetaProposito, string? notas)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("El canal de gestión debe pertenecer a un centro.", nameof(centroId));

        CentroId = centroId;
        Tipo = tipo;
        EstablecerEtiquetaProposito(etiquetaProposito);
        EstablecerNotas(notas);
    }

    public static CanalGestionDocumental DePlataforma(
        Guid centroId, string etiquetaProposito, Guid proveedorPlataformaCaeId, string? urlAcceso,
        string? usuario, string? contrasena, string? notas = null)
    {
        var canal = new CanalGestionDocumental(centroId, TipoCanalGestion.Plataforma, etiquetaProposito, notas);
        canal.EstablecerProveedorPlataformaCae(proveedorPlataformaCaeId);
        canal.EstablecerUrlAcceso(urlAcceso);
        canal.ActualizarCredenciales(usuario, contrasena);
        return canal;
    }

    public static CanalGestionDocumental PorEmail(
        Guid centroId, string etiquetaProposito, string emailsDestinatarios, string? nombreContacto, string? notas = null)
    {
        var canal = new CanalGestionDocumental(centroId, TipoCanalGestion.Email, etiquetaProposito, notas);
        canal.EstablecerEmailsDestinatarios(emailsDestinatarios);
        canal.EstablecerNombreContacto(nombreContacto);
        return canal;
    }

    public void ActualizarPlataforma(string etiquetaProposito, Guid proveedorPlataformaCaeId, string? urlAcceso, string? notas)
    {
        RequerirTipo(TipoCanalGestion.Plataforma);
        EstablecerEtiquetaProposito(etiquetaProposito);
        EstablecerProveedorPlataformaCae(proveedorPlataformaCaeId);
        EstablecerUrlAcceso(urlAcceso);
        EstablecerNotas(notas);
    }

    public void ActualizarCredenciales(string? usuario, string? contrasena)
    {
        RequerirTipo(TipoCanalGestion.Plataforma);
        Usuario = usuario;
        Contrasena = contrasena;
    }

    public void ActualizarEmail(string etiquetaProposito, string emailsDestinatarios, string? nombreContacto, string? notas)
    {
        RequerirTipo(TipoCanalGestion.Email);
        EstablecerEtiquetaProposito(etiquetaProposito);
        EstablecerEmailsDestinatarios(emailsDestinatarios);
        EstablecerNombreContacto(nombreContacto);
        EstablecerNotas(notas);
    }

    /// <summary>
    /// Quién deja de ser principal cuando otro lo pasa a ser es decisión del
    /// Command (necesita ver los N canales del Centro), no de la entidad.
    /// </summary>
    public void MarcarComoPrincipal() => EsPrincipal = true;

    public void DejarDeSerPrincipal() => EsPrincipal = false;

    private void RequerirTipo(TipoCanalGestion tipo)
    {
        if (Tipo != tipo)
            throw new InvalidOperationException($"Este canal de gestión es de tipo {Tipo}, no {tipo}.");
    }

    private void EstablecerEtiquetaProposito(string etiquetaProposito)
    {
        if (string.IsNullOrWhiteSpace(etiquetaProposito))
            throw new ArgumentException("La etiqueta de propósito es obligatoria.", nameof(etiquetaProposito));

        var normalizado = etiquetaProposito.Trim();

        if (normalizado.Length > LongitudMaximaEtiquetaProposito)
            throw new ArgumentException(
                $"La etiqueta de propósito no puede superar {LongitudMaximaEtiquetaProposito} caracteres.", nameof(etiquetaProposito));

        EtiquetaProposito = normalizado;
    }

    private void EstablecerProveedorPlataformaCae(Guid proveedorPlataformaCaeId)
    {
        if (proveedorPlataformaCaeId == Guid.Empty)
            throw new ArgumentException("La plataforma es obligatoria — resuélvela desde la URL de acceso o elígela manualmente.", nameof(proveedorPlataformaCaeId));

        ProveedorPlataformaCaeId = proveedorPlataformaCaeId;
    }

    private void EstablecerUrlAcceso(string? urlAcceso)
    {
        if (urlAcceso?.Length > LongitudMaximaUrlAcceso)
            throw new ArgumentException($"La URL no puede superar {LongitudMaximaUrlAcceso} caracteres.", nameof(urlAcceso));

        UrlAcceso = urlAcceso;
    }

    private void EstablecerEmailsDestinatarios(string emailsDestinatarios)
    {
        if (string.IsNullOrWhiteSpace(emailsDestinatarios))
            throw new ArgumentException("Los correos de destino son obligatorios.", nameof(emailsDestinatarios));

        var normalizado = emailsDestinatarios.Trim();

        if (normalizado.Length > LongitudMaximaEmailsDestinatarios)
            throw new ArgumentException(
                $"Los correos de destino no pueden superar {LongitudMaximaEmailsDestinatarios} caracteres.", nameof(emailsDestinatarios));

        EmailsDestinatarios = normalizado;
    }

    private void EstablecerNombreContacto(string? nombreContacto)
    {
        if (nombreContacto?.Length > LongitudMaximaNombreContacto)
            throw new ArgumentException(
                $"El nombre de contacto no puede superar {LongitudMaximaNombreContacto} caracteres.", nameof(nombreContacto));

        NombreContacto = nombreContacto;
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas?.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
