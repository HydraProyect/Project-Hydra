using System.Security.Cryptography;
using Microsoft.Playwright;
using PdfSharp.Pdf;

namespace CaeManager.E2ETests;

/// <summary>
/// Datos y utilidades compartidas por los tests E2E — credenciales de los
/// usuarios sembrados por IdentitySeeder/DatosPruebaSeeder (ver esas clases
/// en CaeManager.Infrastructure.Identity / Persistence.Seed) y helpers de
/// Playwright para los patrones repetidos de login/drawer que ya se
/// verificaban a mano con verificar_roles.js.
/// </summary>
public static class Ayudas
{
    public const string EmailAdministrador = "admin@caemanager.local";
    public const string ContrasenaAdministrador = "CaeManager#2026";

    /// <summary>
    /// Misma clave que IdentitySeeder.ClaveTotpAdministradorInicial (ver esa
    /// clase en CaeManager.Infrastructure.Identity) — duplicada aquí en vez
    /// de referenciada porque este proyecto de test no referencia
    /// Infrastructure (mismo criterio que NombreClienteDelegadoDemo); si
    /// cambia allí, este test debe actualizarse también. El Administrador
    /// inicial nace con 2FA activo (P1-13 de docs/business/MATURITY_REVIEW.md),
    /// así que IniciarSesionAsync tiene que poder calcular el código TOTP.
    /// </summary>
    public const string ClaveTotpAdministrador = "JBSWY3DPEHPK3PXP";

    public const string ContrasenaUsuariosPrueba = "Prueba#2026";

    /// <summary>
    /// Nombre del tenant Cliente Delegante que DelegacionDemoSeeder siembra
    /// para el Administrador inicial (ver esa clase en
    /// CaeManager.Infrastructure.Persistence.Seed) — duplicado aquí en vez de
    /// referenciado porque este proyecto de test no referencia Infrastructure
    /// (mismo criterio que EmailAdministradorSegundoTenant); si cambia allí,
    /// este test debe actualizarse también.
    /// </summary>
    public const string NombreClienteDelegadoDemo = "Laboratorios Dexter S.L. (Cliente Delegante demo)";

    public static string EmailPrueba(string rolEnMinusculas, int numero) =>
        $"prueba.{rolEnMinusculas}{numero}@caemanager.local";

    /// <summary>
    /// Cambia el "Cliente activo" (ver SelectorClienteActivo.razor) al
    /// Cliente Delegante indicado por nombre, usando el &lt;select&gt; real de
    /// la interfaz.
    ///
    /// Antes esto navegaba a mano al endpoint, saltándose el selector: el
    /// cambio lo disparaba un @onchange de Blazor que exigía tener el circuito
    /// ya interactivo (ida y vuelta por SignalR) y resultó intermitente — a
    /// veces el evento no llegaba a dispararse desde Playwright y el cliente
    /// activo no cambiaba sin dar ningún error visible. Desde el arreglo de
    /// M-8 el selector es un &lt;form&gt; HTML que hace POST, así que el envío
    /// lo hace el navegador sin depender de SignalR y se puede ejercitar la
    /// interfaz de verdad — que además es lo que hace el usuario.
    /// </summary>
    public static async Task CambiarClienteActivoAsync(IPage page, string baseUrl, string nombreCliente)
    {
        var opcion = page.Locator(".selector-cliente-activo option", new PageLocatorOptions { HasText = nombreCliente });
        var tenantId = await opcion.GetAttributeAsync("value");

        await page.SelectOptionAsync(".selector-cliente-activo", new SelectOptionValue { Value = tenantId });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    public static async Task IniciarSesionAsync(IPage page, string baseUrl, string email, string password)
    {
        await page.GotoAsync($"{baseUrl}/cuenta/iniciar-sesion");
        await page.FillAsync("#email", email);
        await page.FillAsync("#password", password);
        await page.ClickAsync("button[type=\"submit\"]");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Solo el Administrador inicial tiene 2FA activo hoy (P1-13 de
        // docs/business/MATURITY_REVIEW.md) — el resto de cuentas de prueba
        // pasan de largo por esta rama y siguen directas al dashboard.
        if (page.Url.Contains("/cuenta/verificar-2fa"))
        {
            await page.FillAsync("#codigo", GenerarCodigoTotp(ClaveTotpAdministrador));
            await page.ClickAsync("button[type=\"submit\"]");
        }

        await page.Locator(".nav-principal").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// TOTP de 6 dígitos (RFC 6238, HMAC-SHA1, paso de 30s) — el mismo
    /// algoritmo que <c>UserManager.VerifyTwoFactorTokenAsync</c> valida del
    /// lado servidor vía <c>AuthenticatorTokenProvider</c>. Sin paquete
    /// nuevo: es la única forma de que este proyecto de test calcule el
    /// código del Administrador inicial (ver ClaveTotpAdministrador) sin
    /// acceso a base de datos ni a Infrastructure.
    /// </summary>
    public static string GenerarCodigoTotp(string claveBase32, DateTimeOffset? momento = null)
    {
        var clave = DescodificarBase32(claveBase32);
        var contador = (long)(momento ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;

        var contadorBytes = BitConverter.GetBytes(contador);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(contadorBytes);

        using var hmac = new HMACSHA1(clave);
        var hash = hmac.ComputeHash(contadorBytes);

        var desplazamiento = hash[^1] & 0x0F;
        var codigoBinario =
            ((hash[desplazamiento] & 0x7F) << 24) |
            ((hash[desplazamiento + 1] & 0xFF) << 16) |
            ((hash[desplazamiento + 2] & 0xFF) << 8) |
            (hash[desplazamiento + 3] & 0xFF);

        return (codigoBinario % 1_000_000).ToString("D6");
    }

    private static byte[] DescodificarBase32(string base32)
    {
        const string alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bitsEnBuffer = 0;

        foreach (var caracter in base32.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alfabeto.IndexOf(caracter);
            bitsEnBuffer += 5;
            if (bitsEnBuffer < 8) continue;

            bitsEnBuffer -= 8;
            bytes.Add((byte)((buffer >> bitsEnBuffer) & 0xFF));
        }

        return bytes.ToArray();
    }

    /// <summary>
    /// page.GotoAsync hace una navegación real de navegador (no un
    /// enrutado del lado cliente de Blazor) — cada llamada tira abajo el
    /// circuit de SignalR y lo reconecta desde cero. Si se interactúa
    /// (clic, fill…) justo después de GotoAsync sin esperar a que el
    /// circuit reconecte, el elemento ya está en el DOM (prerenderizado)
    /// pero su @onclick todavía no está cableado del lado servidor, así
    /// que el clic no hace nada — un timeout posterior en la siguiente
    /// espera, no un error inmediato. Esperar a "networkidle" (sin
    /// conexiones activas ~500ms, lo que cubre el handshake del
    /// WebSocket) es el mismo patrón ya usado con éxito en las
    /// verificaciones manuales previas de este proyecto.
    /// </summary>
    public static async Task NavegarYEsperarAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    /// <summary>
    /// Genera un PDF de una página válido con PDFsharp — la misma librería
    /// que usa ConversorArchivosPdf en producción para combinar/leer los
    /// archivos subidos — para que el flujo de subida real (import vía
    /// PdfReader.Open) tenga un archivo que de verdad pueda parsear, en vez
    /// de unos bytes con cabecera "%PDF" pero sin estructura real. Página en
    /// blanco a propósito, sin texto: dibujar texto requiere un
    /// IFontResolver (ver EmbeddedFontResolver de CaeManager.Web, registrado
    /// en su propio Program.cs) que este proceso de test, al no arrancar esa
    /// app in-process, nunca tiene configurado — una página vacía sigue
    /// siendo un PDF perfectamente válido y parseable, y es lo único que
    /// hace falta para probar el flujo de subida.
    /// </summary>
    public static string GenerarPdfDePruebaEnDisco(string nombreArchivo = "documento-prueba.pdf")
    {
        using var documento = new PdfDocument();
        documento.AddPage();

        var ruta = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{nombreArchivo}");
        documento.Save(ruta);
        return ruta;
    }

    /// <summary>Mismo algoritmo que DatosPruebaSeeder.GenerarCifValido (ver ese archivo) — CIF sintético válido, letra 'B'.</summary>
    public static string GenerarCifValido(int numero)
    {
        var digitos = numero.ToString("D7");
        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoControl = residuo == 0 ? 0 : 10 - residuo;
        return $"B{digitos}{digitoControl}";
    }

    /// <summary>Mismo algoritmo que DatosPruebaSeeder.GenerarDniValido — DNI sintético con dígito de control válido.</summary>
    public static string GenerarDniValido(int numero)
    {
        const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
        return $"{numero:D8}{letrasControl[numero % 23]}";
    }
}
