using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Identity;

/// <summary>Usuario interno de CAE Manager. Extiende Identity solo con lo que ya se necesita en Fase 0.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string NombreCompleto { get; set; } = string.Empty;
    public TemaPreferido Tema { get; set; } = TemaPreferido.Sistema;

    /// <summary>
    /// Solo relevante para el rol GestorCae — el CoordinadorCae al que
    /// reporta (ver Roles.cs y IAlcanceDatosService). Un GestorCae sin
    /// coordinador asignado solo es visible para Administrador/DireccionCae
    /// hasta que se le asigne uno. Guid suelto (sin navegación EF) — mismo
    /// patrón que EntidadBase.EliminadoPorUsuarioId, porque Identity no
    /// tiene FK real hacia sí mismo por conveniencia de migraciones.
    /// </summary>
    public Guid? CoordinadorUsuarioId { get; set; }

    /// <summary>
    /// Solo relevante para el rol Cliente — el Cliente de negocio
    /// (CaeManager.Domain.Clientes.Cliente) que este usuario representa.
    /// Se vincula por CIF al crear el usuario (ver Usuarios.razor). Un
    /// usuario Cliente sin ClienteId no ve ningún dato (alcance vacío, no
    /// alcance total) — nunca se interpreta null aquí como "sin
    /// restricción", a diferencia del resto de roles.
    /// </summary>
    public Guid? ClienteId { get; set; }

    /// <summary>
    /// True cuando un Administrador acaba de crear este usuario con una
    /// contraseña temporal (ver Usuarios.razor.cs) y todavía no la ha
    /// cambiado por una propia. Se comprueba tras cada inicio de sesión
    /// (ver Login.razor) y se hace cumplir en cada navegación desde
    /// MainLayout — nunca se marca en true para el Administrador inicial
    /// (IdentitySeeder) ni para los usuarios de DatosPruebaSeeder, porque
    /// ambos usan una contraseña conocida y elegida deliberadamente, no una
    /// temporal de un tercero.
    /// </summary>
    public bool DebeCambiarContrasena { get; set; }
}
