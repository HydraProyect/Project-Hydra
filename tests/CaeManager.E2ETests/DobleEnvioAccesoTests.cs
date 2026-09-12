using System.Collections.Generic;

using Microsoft.Playwright;

namespace CaeManager.E2ETests;

/// <summary>
/// <b>¿Qué pasa de verdad cuando alguien envía dos veces un formulario de la
/// escena de acceso?</b>
///
/// <para>
/// Las cuatro pantallas de cuenta (2FA, olvidé/restablecer/cambiar contraseña)
/// son SSR estática (sin <c>@rendermode</c>): cada POST reconstruye la
/// instancia del componente, con su propia bandera a <c>false</c>. Una guarda
/// de reentrada en el servidor —<c>if (_enviando) return</c>— no puede ver la
/// segunda petición: medido con una sonda propia (dos POST concurrentes con
/// esa guarda produjeron dos instancias, ambas ejecutando el manejador, cero
/// bloqueos). Solo un test que mire el tráfico de red real, no bUnit (una
/// sola instancia entre los dos envíos, así que un bloqueo que en producción
/// no ocurre daría verde igual), puede decir qué pasa de verdad.
/// </para>
///
/// <para>
/// <b>Lo que este fichero mide, y lo que NO hay que darle crédito.</b> Un
/// doble clic sintético (<c>el.click(); el.click();</c>, sin esperar entre
/// uno y otro) en la MISMA pestaña llega al servidor como una sola petición
/// POST — pero eso ya es así con <c>wwwroot/js/acceso-doble-envio.js</c>
/// completamente retirado de la página: medido quitando el guion del todo,
/// mismo resultado. La causa es que el envío de estos formularios hace una
/// recarga completa del documento (se comprobó que una variable puesta en
/// <c>window</c> antes del envío desaparece después: no es una navegación
/// «enhanced» que preserve el JS), y esa recarga serializa cualquier segundo
/// clic dentro de la misma pestaña antes de que corra ningún guion de la
/// página. El primer test de abajo documenta ese comportamiento tal cual es
/// hoy — útil como red de regresión si algún día cambia — sin atribuírselo a
/// <c>acceso-doble-envio.js</c>, que aquí no tiene nada que demostrar.
/// </para>
///
/// <para>
/// <b>El hueco real, medido igual de directo.</b> Dos pestañas (o dos
/// contextos de navegador) distintas SÍ producen dos POST reales: cada una
/// tiene su propio documento, su propia recarga, su propio guion — nada las
/// serializa entre sí. Si cerrarlo con un cerrojo de servidor para estas tres
/// pantallas (en vez de solo LoginCon2fa, que sí lo lleva aparte por el coste
/// real de un doble intento sobre el contador de bloqueo de la cuenta) es
/// **pendiente de decisión del propietario, no decidido todavía** — el
/// tercer test de abajo deja constancia del hueco tal cual existe hoy sin
/// afirmar que esté aceptado, y por eso está marcado <c>Skip</c> en vez de
/// aserción en verde: un test verde que dice "hueco conocido y aceptado" se
/// cita después como si la decisión ya estuviera tomada, y no lo está.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class DobleEnvioAccesoTests(WebAppFixture fixture)
{
    private const string CorreoInexistente = "nadie-e2e-doble-envio@consultora.es";

    [Fact]
    public async Task El_doble_clic_en_olvide_contrasena_en_una_pestana_solo_llega_como_una_peticion_POST()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await page.GotoAsync($"{fixture.BaseUrl}/cuenta/olvide-contrasena");
        await page.FillAsync("#email", CorreoInexistente);

        var boton = page.Locator("form button[type=submit]");
        await Assertions.Expect(boton).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var peticiones = new List<string>();
        void RegistrarPeticion(object? _, IRequest req)
        {
            if (req.Method == "POST" && req.Url.Contains("/cuenta/olvide-contrasena"))
                peticiones.Add($"{req.Method} {req.Url}");
        }
        page.Request += RegistrarPeticion;

        await boton.EvaluateAsync("el => { el.click(); el.click(); }");

        await Assertions.Expect(page.GetByText("Revisa tu correo")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        page.Request -= RegistrarPeticion;

        Assert.True(peticiones.Count == 1,
            "El doble clic sobre \"Enviarme el enlace\" EN LA MISMA PESTAÑA tenía que producir una sola "
            + $"petición POST a /cuenta/olvide-contrasena; se registraron {peticiones.Count}: "
            + string.Join("; ", peticiones)
            + ". Si esto cambia a 2, cambió el mecanismo de envío del formulario (por ejemplo, empezó a "
            + "funcionar la navegación «enhanced» en vez de la recarga completa) y acceso-doble-envio.js "
            + "pasa de ser defensa en profundidad a ser la única barrera real: revisar este comentario.");
    }

    /// <summary>
    /// Control positivo: si el test de arriba diera verde con cero peticiones
    /// registradas en vez de una, no demostraría nada — significaría que
    /// Playwright no ve el tráfico, no que el envío esté serializado (mismo
    /// criterio que <c>SeleccionSobreviveAlCircuitoTests</c>). Este test
    /// prueba que el listener SÍ cuenta más de una petición cuando de verdad
    /// las hay: el botón de reenvío es un segundo <c>&lt;form&gt;</c> real,
    /// con el mismo <c>FormName</c>, pensado para un segundo POST deliberado
    /// — y produce una segunda petición real, distinta del clic doble.
    /// </summary>
    [Fact]
    public async Task Control_positivo_el_reenvio_explicito_SI_dispara_una_segunda_peticion_POST()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();

        await page.GotoAsync($"{fixture.BaseUrl}/cuenta/olvide-contrasena");
        await page.FillAsync("#email", CorreoInexistente);
        await page.Locator("form button[type=submit]").ClickAsync();

        await Assertions.Expect(page.GetByText("Revisa tu correo")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var peticiones = new List<string>();
        void RegistrarPeticion(object? _, IRequest req)
        {
            if (req.Method == "POST" && req.Url.Contains("/cuenta/olvide-contrasena"))
                peticiones.Add($"{req.Method} {req.Url}");
        }
        page.Request += RegistrarPeticion;

        await page.Locator("button.acceso-enlace-boton").ClickAsync();

        await Assertions.Expect(page.GetByText("Revisa tu correo")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        page.Request -= RegistrarPeticion;

        Assert.True(peticiones.Count == 1,
            "El reenvío explícito, tras el primer envío ya completado, tenía que producir su propia "
            + $"petición POST; se registraron {peticiones.Count}. Si esto da cero, el listener de arriba "
            + "no está midiendo tráfico real y su verde no demuestra nada.");
    }

    /// <summary>
    /// Documenta el hueco real: dos pestañas no comparten ningún estado de
    /// JavaScript, así que ninguna guarda de cliente puede serializarlas.
    ///
    /// Marcado <c>Skip</c> a propósito, no <c>[Fact]</c> en verde: una
    /// aserción que pasa hoy porque el hueco sigue abierto es exactamente el
    /// patrón que CLAUDE.md § 6 desaconseja — un test verde que codifica
    /// deliberadamente el contrato que no se quiere, y que además alguien
    /// podría citar más tarde como si "dos POST reales" fuera un
    /// comportamiento aceptado, cuando es una decisión PENDIENTE del
    /// propietario, no tomada. El código queda aquí, ejecutable a mano o
    /// quitando el <c>Skip</c>, para cuando haya que volver a medir esto —
    /// pero no corre en CI dando una falsa sensación de cobertura verde sobre
    /// algo que no está resuelto.
    /// </summary>
    [Fact(Skip = "Hueco conocido, pendiente de decisión del propietario (¿cerrojo de servidor también para " +
        "OlvideContrasena/RestablecerContrasena/CambiarContrasena, o se acepta el riesgo?). No convertir en " +
        "aserción en verde hasta que exista esa decisión: ver acceso-doble-envio.js y el PR que introdujo " +
        "este fichero.")]
    public async Task Dos_pestanas_distintas_SI_producen_dos_peticiones_POST_hueco_conocido_sin_resolver()
    {
        await using var contexto1 = await fixture.Browser.NewContextAsync();
        await using var contexto2 = await fixture.Browser.NewContextAsync();
        var page1 = await contexto1.NewPageAsync();
        var page2 = await contexto2.NewPageAsync();

        var peticiones = new System.Collections.Concurrent.ConcurrentBag<string>();
        void Registrar(object? _, IRequest req)
        {
            if (req.Method == "POST" && req.Url.Contains("/cuenta/olvide-contrasena"))
                peticiones.Add(req.Url);
        }
        page1.Request += Registrar;
        page2.Request += Registrar;

        await page1.GotoAsync($"{fixture.BaseUrl}/cuenta/olvide-contrasena");
        await page2.GotoAsync($"{fixture.BaseUrl}/cuenta/olvide-contrasena");
        await page1.FillAsync("#email", CorreoInexistente);
        await page2.FillAsync("#email", CorreoInexistente);

        var boton1 = page1.Locator("form button[type=submit]");
        var boton2 = page2.Locator("form button[type=submit]");
        await Assertions.Expect(boton1).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(boton2).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        await Task.WhenAll(boton1.ClickAsync(), boton2.ClickAsync());

        await Assertions.Expect(page1.GetByText("Revisa tu correo")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(page2.GetByText("Revisa tu correo")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        Assert.True(peticiones.Count == 2,
            "Hueco conocido, pendiente de decisión del propietario (no resuelto en este incremento): dos "
            + "pestañas distintas no comparten guarda de cliente. Se esperaban 2 peticiones POST reales; se "
            + $"registraron {peticiones.Count}. Si esto baja a 1, algo empezó a serializarlas — actualizar "
            + "este test y el comentario de la clase, no borrarlo.");
    }
}
