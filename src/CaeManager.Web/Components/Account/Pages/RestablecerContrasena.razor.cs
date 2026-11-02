using System.ComponentModel.DataAnnotations;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Components.Account.Recursos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Components.Account.Pages;

public partial class RestablecerContrasena : ComponentBase
{
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private SignInManager<ApplicationUser> SignInManager { get; set; } = default!;
    [Inject] private IRegistroSesionesUsuario Sesiones { get; set; } = default!;
    [Inject] private IOptions<IdentityOptions> OpcionesIdentity { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private ILoggerFactory LoggerFactory { get; set; } = default!;
    [Inject] private IEleccionLiderService CerrojoCredencial { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado Seleccion { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosCuenta> Textos { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "userId")]
    private string? UserId { get; set; }

    [SupplyParameterFromQuery(Name = "code")]
    private string? Code { get; set; }

    [SupplyParameterFromForm]
    private DatosEntrada? Entrada { get; set; }

    private ApplicationUser? _usuario;
    private string? _token;
    private string? _correoCuenta;
    private bool _enlaceInvalido;
    private bool _completado;
    private string? _mensajeError;

    private string? ExplicacionCuenta => _correoCuenta is null ? null : $"Para la cuenta {_correoCuenta}.";

    // Aquí vivía PuedeGuardar, que gobernaba el disabled del botón. Retirado
    // en vez de dejarlo sin uso: era una barrera que NUNCA llegaba a
    // evaluarse con datos —esta página es SSR estática y solo se renderiza una
    // vez, con la contraseña vacía—, así que el botón nacía deshabilitado y
    // nada podía habilitarlo. Dejar la propiedad ahí invitaría a volver a
    // cablearla al botón y reintroducir el mismo bloqueo.
    //
    // Lo que sí comprobaba —que las dos contraseñas coincidan— está ahora en
    // GuardarAsync, que es donde corre de verdad. Y la política la aplica el
    // propio GuardarAsync antes de revocar sesiones, y otra vez
    // ResetPasswordAsync al cambiar la contraseña.
    //
    // Los requisitos se anuncian con RequisitosContrasena, que los marca en
    // vivo desde el navegador; la coincidencia también se anuncia en vivo.

    protected override void OnInitialized() => Entrada ??= new DatosEntrada();

    protected override async Task OnInitializedAsync()
    {
        if (DenegarSesionPrivilegiada()) return;
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(Code))
        {
            _enlaceInvalido = true;
            return;
        }

        // Antes de que exista Tenant: la cuenta se busca por su clave bajo la RLS
        // de AspNetUsers (ver AmbitoIdentificacionSinTenant, P1-M1).
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        _usuario = await UserManager.FindByIdAsync(UserId);
        if (_usuario is null)
        {
            _enlaceInvalido = true;
            return;
        }

        try
        {
            _token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(Code));
        }
        catch (FormatException)
        {
            _enlaceInvalido = true;
            return;
        }

        _correoCuenta = _usuario.Email;
    }

    // Coherencia con el rol de solo lectura que TenantRlsConnectionInterceptor adopta con esta misma coordenada:
    // la guarda da un mensaje y un aviso en vez del fallo de Postgres (ASVS 4.0.3 V4.1.5). Solo actúa con la sesión
    // iniciada y la selección viva: sin sesión, o con una selección caducada, retirada o de otro usuario, es null.
    private bool DenegarSesionPrivilegiada()
    {
        if (Seleccion.SesionPrivilegiadaIdSeleccionada is { } sesionId)
        {
            LoggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog).LogWarning(
                "Restablecimiento de contraseña denegado durante la Sesión Privilegiada {SesionPrivilegiadaId}.", sesionId);
            _mensajeError = Textos["SesionPrivilegiadaRestablecerContrasena"];
            return true;
        }
        return false;
    }

    private async Task GuardarAsync()
    {
        if (DenegarSesionPrivilegiada()) return;
        if (_usuario is null || _token is null || Entrada is null) return;

        // Antes de que exista Tenant: la cuenta se busca por su clave bajo la RLS
        // de AspNetUsers (ver AmbitoIdentificacionSinTenant, P1-M1).
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();

        // La coincidencia se comprueba AQUÍ, no en el disabled del botón.
        //
        // Antes la única barrera era `PuedeGuardar`, y no funcionaba: esta
        // página es SSR estática, así que ese disabled se evaluaba una sola vez
        // en servidor con la contraseña vacía y el botón nacía deshabilitado
        // para siempre. Al quitarlo hay que poner la comprobación donde sí
        // corre, o `ResetPasswordAsync` establecería `ContrasenaNueva`
        // ignorando en silencio el campo de confirmación — y el usuario se
        // quedaría con una contraseña distinta de la que creyó escribir dos
        // veces, sin ningún aviso. `DatosEntrada` no lleva `[Compare]`, así que
        // el validador de anotaciones tampoco lo cubre.
        //
        // Mismo sitio y mismo patrón que CambiarContrasena.razor.
        if (Entrada.ContrasenaNueva != Entrada.ConfirmarContrasenaNueva)
        {
            _mensajeError = "La confirmación no coincide con la contraseña nueva.";
            return;
        }

        _mensajeError = null;

        var logger = LoggerFactory.CreateLogger(AuditoriaAutenticacion.CategoriaLog);

        // Prevalidación del token ANTES de competir por el cerrojo: un token
        // inventado con un userId real (el userId viaja en la URL sin cifrar)
        // no debe poder ocupar el cerrojo, ni siquiera brevemente, mientras el
        // titular real de la cuenta intenta restablecer su propia contraseña.
        // Mismo criterio de verificación que usa ResetPasswordAsync por
        // dentro — se duplica aquí para decidir ANTES del cerrojo, no en vez
        // de la comprobación de dentro: ResetPasswordAsync la repite y sigue
        // siendo la que de verdad autoriza el cambio de contraseña. Sí es lo
        // que autoriza la revocación previa de sesiones: esa corre antes de
        // ResetPasswordAsync, y sin este token no debe ocurrir.
        var tokenValido = await UserManager.VerifyUserTokenAsync(
            _usuario, UserManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose, _token);
        if (!tokenValido)
        {
            logger.LogWarning("Restablecimiento de contraseña rechazado (token inválido o caducado, prevalidación): {UsuarioId}", _usuario.Id);
            _enlaceInvalido = true;
            return;
        }

        // Prevalidación de la política, por lo mismo que la del token: una
        // contraseña que Identity va a rechazar no cierra ninguna sesión. Son
        // los validadores que ResetPasswordAsync aplica por dentro, en el mismo
        // orden respecto al token, con el mismo mensaje.
        var incumplidos = new List<IdentityError>();
        foreach (var validador in UserManager.PasswordValidators)
            incumplidos.AddRange((await validador.ValidateAsync(UserManager, _usuario, Entrada.ContrasenaNueva)).Errors);
        if (incumplidos.Count > 0)
        {
            _mensajeError = string.Join(" ", incumplidos.Select(e => e.Description));
            return;
        }

        // Cerrojo no bloqueante compartido con CambiarContrasena.razor bajo la
        // misma clave `credencial:{userId}` — las dos pantallas mutan la
        // misma credencial de Identity, así que un restablecimiento y un
        // cambio autenticado que coincidan en el tiempo para el mismo usuario
        // también deben excluirse entre sí, no solo dos restablecimientos
        // entre sí. NO comparte clave con `verificar-2fa:{userId}` (cerrojo
        // distinto, en LoginCon2fa): son operaciones distintas y no hay
        // motivo para que una bloquee a la otra.
        //
        // Se adquiere DESPUÉS de resolver el usuario y el token (arriba, en
        // OnInitializedAsync) y ANTES de tocar ResetPasswordAsync: el
        // competidor se rechaza sin gastar ningún intento contra Identity.
        IdentityResult? resultadoObtenido = null;
        var ejecutado = await CerrojoCredencial.IntentarEjecutarComoLiderAsync(
            $"credencial:{_usuario.Id}",
            async _ =>
            {
                // ASVS 4.0.3 V3.3.3. Las filas de sesión se revocan ANTES de cambiar la credencial: la
                // revocación y Identity van por contextos distintos, sin transacción común, y al revés un
                // fallo aquí dejaría la contraseña cambiada, el enlace gastado y las sesiones vivas; una
                // cookie aún sin revalidar podría salir de RefreshSignInAsync con el sello nuevo. Así, si
                // falla, se propaga sin haber cambiado nada y este enlace sigue valiendo para reintentar.
                // Si lo que falla es ResetPasswordAsync, la cuenta queda sin sesiones y con su contraseña,
                // el lado seguro. CerrarLasDemasAsync porque no rota el sello: el token depende de él, y
                // CerrarTodasAsync lo invalidaría antes de usarlo. Sin sesión conservada, revoca todas.
                await Sesiones.CerrarLasDemasAsync(_usuario.Id, null);

                // Se fija ANTES de ResetPasswordAsync, sobre la misma
                // instancia de _usuario que la operación va a actualizar:
                // así Identity persiste el fin del cambio obligatorio en la
                // MISMA actualización que la contraseña, sin un UpdateAsync
                // posterior separado. Ese segundo UpdateAsync era el estado
                // parcial: si fallaba, la contraseña ya había cambiado pero
                // DebeCambiarContrasena seguía en true. Si ResetPasswordAsync
                // falla (token inválido, política de contraseña), no se
                // persiste en base de datos ni la contraseña ni esta bandera
                // — porque Identity no llega a guardar.
                _usuario.DebeCambiarContrasena = false;
                resultadoObtenido = await UserManager.ResetPasswordAsync(_usuario, _token, Entrada.ContrasenaNueva);
            },
            CancellationToken.None);

        if (!ejecutado)
        {
            logger.LogWarning(
                "Restablecimiento de contraseña rechazado, ya había una operación de credencial en curso: {UsuarioId}",
                _usuario.Id);
            _mensajeError = "Ya hay una operación sobre esta cuenta en curso. Espera un momento e inténtalo de nuevo.";
            return;
        }

        // ejecutado == true garantiza que el delegado corrió y asignó el resultado.
        var resultado = resultadoObtenido!;

        if (!resultado.Succeeded)
        {
            // Un token caducado o ya usado cae aquí igual que un fallo de
            // política de contraseña — no se distingue el motivo exacto
            // en el mensaje (mismo criterio de no-enumeración que
            // Login.razor), pero si el problema es el token se ofrece
            // directamente pedir uno nuevo.
            if (resultado.Errors.Any(e => e.Code is "InvalidToken"))
            {
                logger.LogWarning("Restablecimiento de contraseña rechazado (token inválido o caducado): {UsuarioId}", _usuario.Id);
                _enlaceInvalido = true;
                return;
            }

            _mensajeError = string.Join(" ", resultado.Errors.Select(e => e.Description));
            return;
        }

        logger.LogInformation("Restablecimiento de contraseña correcto: {UsuarioId}", _usuario.Id);

        // ASVS 4.0.3 V3.3.3. Segunda pasada, tras la previa del cerrojo: se lleva lo que se haya abierto con
        // la contraseña antigua entre las dos. Rotar el sello no basta: durante el intervalo de revalidación,
        // una cookie de esa sesión podría llegar a RefreshSignInAsync (alta de TOTP) y salir con el sello
        // nuevo. Un fallo se propaga, como en CierreSesionEnServidor: la página no dice «actualizada» con
        // sesiones abiertas. La contraseña ya cambió y este enlace ya no vuelve a servir, pero solo quedan
        // sin revocar las de esa ventana, hasta otro restablecimiento con un enlace nuevo.
        await Sesiones.CerrarTodasAsync(_usuario.Id);

        await SignInManager.SignOutAsync();
        _completado = true;
    }

    private sealed class DatosEntrada
    {
        [Required(ErrorMessage = "Introduce la contraseña nueva.")]
        public string ContrasenaNueva { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirma la contraseña nueva.")]
        public string ConfirmarContrasenaNueva { get; set; } = string.Empty;
    }
}
