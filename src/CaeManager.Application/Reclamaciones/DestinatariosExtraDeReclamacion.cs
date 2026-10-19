using CaeManager.Domain.Common;
using CaeManager.Domain.Reclamaciones;

namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Los destinatarios que el Gestor CAE añade a mano en el cajón de «Pedir» / «Reclamar en lote», además de los contactos de la
/// agenda del Cliente empresarial: direcciones <see cref="Adicionales"/> (reciben el correo como cualquier contacto) y direcciones
/// <see cref="EnCopia"/> (CC). Ninguna se guarda en la agenda: existen solo para este envío y quedan en el registro de la reclamación.
/// </summary>
public sealed record DestinatariosExtraDeReclamacion(IReadOnlyList<string> Adicionales, IReadOnlyList<string> EnCopia)
{
    public static readonly DestinatariosExtraDeReclamacion Ninguno = new([], []);

    /// <summary>Máximo de direcciones manuales por lista y envío: el cajón es para añadir a alguien, no para importar una agenda.</summary>
    public const int MaximoPorLista = 10;

    // Marcador neutro (no un texto localizado): se persiste en el registro y se lee en cualquier idioma de la interfaz.
    private const string SufijoCopia = " (CC)";

    /// <summary>
    /// Valida y normaliza lo que pide el llamador, SIN fiarse de la UI: formato de correo, sin repetidos (ni entre sí ni con los contactos
    /// de la agenda que ya reciben el envío) y un tope de tamaño. Cualquier problema hace fallar el envío entero antes de mandar nada
    /// (mismo «todo o nada» que con los documentos y los contactos): un correo a un tercero no se puede deshacer.
    /// </summary>
    /// <param name="correosDeAgenda">Los correos de agenda que ya van a recibir el envío (los contactos marcados, ya resueltos).</param>
    public static Result<DestinatariosExtraDeReclamacion> Resolver(
        IReadOnlyList<string>? adicionales, IReadOnlyList<string>? enCopia, IReadOnlyList<string> correosDeAgenda)
    {
        if ((adicionales is null or { Count: 0 }) && (enCopia is null or { Count: 0 }))
            return Result.Exito(Ninguno);

        var vistos = new HashSet<string>(correosDeAgenda, StringComparer.OrdinalIgnoreCase);

        var adicionalesResueltos = Normalizar(adicionales, vistos);
        if (adicionalesResueltos.EsFallido)
            return Result.Fallo<DestinatariosExtraDeReclamacion>(adicionalesResueltos.Error);

        var copiaResuelta = Normalizar(enCopia, vistos);
        if (copiaResuelta.EsFallido)
            return Result.Fallo<DestinatariosExtraDeReclamacion>(copiaResuelta.Error);

        var extra = new DestinatariosExtraDeReclamacion(adicionalesResueltos.Valor, copiaResuelta.Valor);

        // El registro guarda a todos en una sola columna de longitud acotada: se comprueba ANTES de enviar, porque un destinatario que no
        // cabe haría fallar el guardado cuando el correo ya ha salido.
        if (!CabeEnElRegistro([.. correosDeAgenda, .. extra.Adicionales], extra.EnCopia))
        {
            return Result.Fallo<DestinatariosExtraDeReclamacion>(Error.Crear(
                "Reclamacion.DemasiadosDestinatarios",
                "Hay demasiados destinatarios para registrar el envío. Desmarca algún contacto de la agenda o quita alguno de los que has añadido a mano."));
        }

        return Result.Exito(extra);
    }

    /// <summary>Si el envío entero cabe en <see cref="ReclamacionDocumental.DestinatarioEmail"/>. La UI lo usa para avisar al añadir, no al enviar.</summary>
    public static bool CabeEnElRegistro(IEnumerable<string> para, IEnumerable<string> enCopia) =>
        FormatoDeRegistro(para, enCopia).Length <= ReclamacionDocumental.LongitudMaximaDestinatarioEmail;

    /// <summary>
    /// Cómo queda el envío en <see cref="ReclamacionDocumental.DestinatarioEmail"/>: los destinatarios separados por «; » y los de copia
    /// con el sufijo «(CC)», para que el historial distinga a quién se le pidió y a quién se le copió.
    /// </summary>
    public static string FormatoDeRegistro(IEnumerable<string> para, IEnumerable<string> enCopia) =>
        string.Join("; ", para.Concat(enCopia.Select(c => c + SufijoCopia)));

    private static Result<IReadOnlyList<string>> Normalizar(IReadOnlyList<string>? entradas, HashSet<string> vistos)
    {
        var resultado = new List<string>();
        if (entradas is null)
            return Result.Exito<IReadOnlyList<string>>(resultado);

        if (entradas.Count > MaximoPorLista)
        {
            return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                "Reclamacion.DemasiadosDestinatarios", $"No puedes añadir a mano más de {MaximoPorLista} correos en cada lista."));
        }

        foreach (var entrada in entradas)
        {
            var correo = entrada?.Trim() ?? string.Empty;
            if (!EsCorreoUtilizable(correo))
            {
                return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                    "Reclamacion.CorreoNoValido", $"«{correo}» no es un correo válido."));
            }

            if (!vistos.Add(correo))
            {
                return Result.Fallo<IReadOnlyList<string>>(Error.Crear(
                    "Reclamacion.CorreoRepetido", $"«{correo}» está repetido: cada correo solo puede aparecer una vez en el envío."));
            }

            resultado.Add(correo);
        }

        return Result.Exito<IReadOnlyList<string>>(resultado);
    }

    /// <summary>
    /// Formato de correo (<see cref="Email"/>: sin nombre visible ni espacios) y un dominio con punto, que descarta «a@b»: no es una
    /// dirección a la que se escriba desde fuera de una red interna. Rechaza además lo que <see cref="Email"/> tolera pero ningún
    /// contacto real escribe: caracteres de control, comillas y corchetes (parte local entrecomillada, dominio literal). No comprueba
    /// que el buzón exista.
    /// </summary>
    public static bool EsCorreoUtilizable(string correo)
    {
        if (string.IsNullOrWhiteSpace(correo) || correo.Length > 254)
            return false;

        if (correo.Any(c => char.IsControl(c) || c is '"' or '[' or ']' or '<' or '>' or '(' or ')' or ','))
            return false;

        try
        {
            var valor = Email.Crear(correo).Valor;
            var dominio = valor[(valor.LastIndexOf('@') + 1)..];
            return dominio.Contains('.') && !dominio.StartsWith('.') && !dominio.EndsWith('.');
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
