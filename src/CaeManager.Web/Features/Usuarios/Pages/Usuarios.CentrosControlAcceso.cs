using CaeManager.Application.Usuarios;

namespace CaeManager.Web.Features.Usuarios.Pages;

public partial class Usuarios
{
    // --- Asignar Centros a una persona con el rol «Control de acceso» (AF-01) ---
    // La asignación vive en una pantalla de Inbound; el Núcleo la abre por URL, sin referenciar sus tipos (ADR-013 § 4.1, regla 3).

    /// <summary>
    /// Solo presentación: una cuenta de portería, activa y ya activada. Quién puede asignar y qué Centros son asignables lo decide
    /// el comando de Inbound (pide <c>GestionarInbound</c>).
    /// </summary>
    private static bool PuedeAsignarCentros(UsuarioListaDto usuario) =>
        usuario.Rol == AutoridadSobreCuentas.RolControlAcceso && !usuario.EsOperadorDelegado && usuario.Activo && !usuario.PendienteActivacion;

    private void AbrirAsignarCentros(UsuarioListaDto usuario) =>
        NavigationManager.NavigateTo($"/inbound/control-acceso/asignar/{usuario.Id}");
}
