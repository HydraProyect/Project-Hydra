using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Catálogo autoalimentado teléfono→Cliente para el enrutamiento de
/// conversaciones WhatsApp nuevas (leg 1 del híbrido): la primera vez que un
/// gestor resuelve el triage de una conversación WhatsApp
/// (AsignarClienteConversacionCommand) se memoriza aquí el vínculo, y la
/// siguiente conversación del mismo teléfono ya se enruta directamente al
/// gestor de cartera del Cliente (Cliente.EjecutivoUsuarioId). No existe
/// ningún otro catálogo de teléfonos en el dominio — este nace vacío y
/// aprende del uso.
/// </summary>
public class ContactoWhatsApp : EntidadConTenant
{
    public const int LongitudMaximaTelefono = 20;
    public const int LongitudMaximaNombre = 200;

    /// <summary>Teléfono en formato E.164 sin espacios (p. ej. +34686543364) — único por tenant.</summary>
    public string Telefono { get; private set; } = string.Empty;

    public Guid ClienteId { get; private set; }

    /// <summary>Nombre de perfil que reporta WhatsApp (contacts[].profile.name) — solo informativo.</summary>
    public string? Nombre { get; private set; }

    private ContactoWhatsApp()
    {
    }

    public ContactoWhatsApp(string telefono, Guid clienteId, string? nombre = null)
    {
        EstablecerTelefono(telefono);
        ReasignarCliente(clienteId);
        ActualizarNombre(nombre);
    }

    public void ReasignarCliente(Guid clienteId)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El contacto debe apuntar a un cliente.", nameof(clienteId));

        ClienteId = clienteId;
    }

    public void ActualizarNombre(string? nombre)
    {
        var normalizado = string.IsNullOrWhiteSpace(nombre) ? null : nombre.Trim();
        if (normalizado is not null && normalizado.Length > LongitudMaximaNombre)
            normalizado = normalizado[..LongitudMaximaNombre];

        Nombre = normalizado;
    }

    private void EstablecerTelefono(string telefono)
    {
        if (string.IsNullOrWhiteSpace(telefono))
            throw new ArgumentException("El contacto debe tener un teléfono.", nameof(telefono));

        var normalizado = telefono.Trim();
        if (normalizado.Length > LongitudMaximaTelefono)
            throw new ArgumentException($"El teléfono no puede superar {LongitudMaximaTelefono} caracteres.", nameof(telefono));

        Telefono = normalizado;
    }
}
