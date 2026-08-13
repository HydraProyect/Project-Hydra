namespace CaeManager.Application.Common;

/// <summary>
/// Nombre comercial del producto, en un único sitio.
///
/// Existe porque el nombre estaba escrito a mano en unas setenta posiciones
/// —títulos de pestaña, cabeceras, asuntos de correo, pies de PDF—, y eso
/// convierte cualquier cambio de marca en una cacería. Ahora es un valor.
///
/// El valor por defecto es el histórico y se puede sustituir por configuración
/// (<c>Marca:Nombre</c>), que es como se activa el nombre nuevo sin escribirlo
/// en el repositorio — recuérdese que este repositorio es público.
///
/// Estado estático mutable, sí: es un compromiso consciente. La alternativa era
/// inyectar un servicio en las treinta y tantas páginas que solo necesitan
/// pintar el nombre en el título. Se escribe una vez al arrancar y no vuelve a
/// cambiar en toda la vida del proceso.
///
/// Límite conocido: <b>esto no sirve para marca blanca por tenant.</b> Si algún
/// día un tenant necesita ver su propio nombre, este valor global deja de valer
/// y hay que resolverlo con el contexto de tenant, no ampliando esta clase.
/// </summary>
public static class Marca
{
    /// <summary>Nombre histórico. Se usa si la configuración no dice otra cosa.</summary>
    public const string PorDefecto = "CAE Manager";

    public static string Nombre { get; private set; } = PorDefecto;

    /// <summary>Se llama una sola vez desde el arranque de la aplicación.</summary>
    public static void Configurar(string? nombre) =>
        Nombre = string.IsNullOrWhiteSpace(nombre) ? PorDefecto : nombre.Trim();
}
