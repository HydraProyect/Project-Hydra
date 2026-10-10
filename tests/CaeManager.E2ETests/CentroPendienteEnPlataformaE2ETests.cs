using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Pendiente en la plataforma CAE del Centro (decisión del 2026-10-10), de punta a punta: un Centro sin plataforma no
/// tiene ese motivo; en cuanto el documento vigente de su Trabajador queda sin subir en la plataforma del Centro, la fila
/// de /centros dice «1 pendiente en la plataforma», con su ventana de contexto, y la ficha 360 del Centro lo repite. Lo
/// que bUnit no ve: que la consulta real (RLS, alcance, documento efectivo, canal del Centro) llega a esas dos pantallas.
///
/// <para>
/// El escenario es propio y se escribe por SQL en el Tenant del Administrador: un Centro nuevo (del primer Cliente
/// empresarial y la primera Empresa ya sembrados en ese Tenant), un Trabajador nuevo de esa Empresa asignado a él y su
/// documento vigente de un tipo que se pide siempre; después, el acceso de plataforma del Centro y la acreditación de ese
/// documento sin subir. Darlos de alta por la interfaz no es lo que se mide, y escribirlos así no dispara ningún envío:
/// no se envía nada. Ningún otro E2E lee este Centro ni este Trabajador.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class CentroPendienteEnPlataformaE2ETests(WebAppFixture fixture)
{
    [Fact]
    public async Task Un_documento_sin_subir_a_la_plataforma_del_Centro_lo_deja_pendiente_en_la_lista_y_en_la_ficha()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreCentro = $"EnPlataforma Centro {sufijo}";
        const string nombreTrabajador = "EnPlataforma";
        var apellidosTrabajador = $"E2E {sufijo}";
        var hoy = Ayudas.HoyDeNegocio();

        // --- Centro sin plataforma, con un Trabajador asignado y su documento vigente ---
        // Requerido y AmbitoAplicacion se guardan como texto; EstadoVigencia 2 = VenceEnFecha; GestionCae 0 = con gestión.
        var documentos = await fixture.EjecutarSqlAsync("""
            WITH tenant AS (
                SELECT "TenantId" AS "Id" FROM "AspNetUsers" WHERE "Email" = @email),
            base AS (
                SELECT c."ClienteId", c."EmpresaId", c."TenantId"
                FROM "Centros" c JOIN tenant ON c."TenantId" = tenant."Id"
                WHERE NOT c."EstaEliminado"
                ORDER BY c."CreadoEnUtc", c."Id" LIMIT 1),
            centro AS (
                INSERT INTO "Centros" ("Id", "ClienteId", "EmpresaId", "TenantId", "Nombre", "GestionCae", "EstaEliminado", "CreadoEnUtc", "Version")
                SELECT gen_random_uuid(), "ClienteId", "EmpresaId", "TenantId", @centro, 0, false, now(), gen_random_uuid() FROM base
                RETURNING "Id", "TenantId", "EmpresaId"),
            trabajador AS (
                INSERT INTO "Trabajadores" ("Id", "TenantId", "EmpresaId", "Nombre", "Apellidos", "Dni", "EstaEliminado", "CreadoEnUtc", "Version")
                SELECT gen_random_uuid(), "TenantId", "EmpresaId", @nombre, @apellidos, @dni, false, now(), gen_random_uuid() FROM centro
                RETURNING "Id", "TenantId"),
            asignacion AS (
                INSERT INTO "Asignaciones" ("Id", "CentroId", "TrabajadorId", "TenantId", "FechaAlta")
                SELECT gen_random_uuid(), centro."Id", trabajador."Id", trabajador."TenantId", @hoy::date FROM centro, trabajador
                RETURNING 1),
            tipo AS (
                SELECT t."Id" FROM "TiposDocumento" t JOIN tenant ON t."TenantId" = tenant."Id"
                WHERE t."Requerido" = 'Si' AND t."AmbitoAplicacion" = 'Trabajador'
                ORDER BY t."Orden", t."Nombre" LIMIT 1)
            INSERT INTO "Documentos"
                ("Id", "TenantId", "TipoDocumentoId", "TrabajadorId", "FechaEmision", "FechaVencimiento", "EstadoVigencia",
                 "EstaEliminado", "CreadoEnUtc", "Version")
            SELECT gen_random_uuid(), trabajador."TenantId", tipo."Id", trabajador."Id", @hoy::date - 5, @hoy::date + 180, 2,
                   false, now(), gen_random_uuid()
            FROM trabajador, tipo, (SELECT count(*) FROM asignacion) AS asignada
            """,
            ("email", Ayudas.EmailAdministrador), ("centro", nombreCentro), ("nombre", nombreTrabajador),
            ("apellidos", apellidosTrabajador), ("dni", Ayudas.GenerarDniValido(88_100_981)), ("hoy", hoy.ToString("yyyy-MM-dd")));
        Assert.Equal(1, documentos);
        var centroId = await fixture.LeerValorSqlAsync("""SELECT "Id"::text FROM "Centros" WHERE "Nombre" = @centro""", ("centro", nombreCentro));

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailAdministrador, Ayudas.ContrasenaAdministrador);

        var urlLista = $"{fixture.BaseUrl}/centros?q={Uri.EscapeDataString(nombreCentro)}";
        var filaCentro = page.Locator(".tarjeta-fila-acordeon").Filter(new LocatorFilterOptions
        {
            Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Asignaciones de {nombreCentro}", Exact = true }),
        });
        var cabeceraFila = filaCentro.Locator(".tarjeta-fila-acordeon-cabecera");

        // --- Sin plataforma: la fila no tiene el motivo (control negativo, con barrera: la fila ya está pintada) ---
        await Ayudas.NavegarYEsperarAsync(page, urlLista);
        await Expect(cabeceraFila.Locator(".ranura-estado-centro")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(cabeceraFila.Locator(".motivo-recuento-pendientes")).ToHaveCountAsync(0);

        // --- El Centro pasa a tener plataforma, con la acreditación de ese documento sin subir ---
        // Tipo 0 = TipoCanalGestion.Plataforma; Estado 0 = PendienteDeSubir y EstadoVigencia 0 = SinConfirmar, como
        // los deja el constructor de AcreditacionDocumentoPlataforma.
        var acreditaciones = await fixture.EjecutarSqlAsync("""
            WITH canal AS (
                INSERT INTO "CanalesGestionDocumental"
                    ("Id", "CentroId", "TenantId", "Tipo", "EsPrincipal", "EstaEliminado", "EtiquetaProposito",
                     "ProveedorPlataformaCaeId", "CreadoEnUtc", "Version")
                SELECT gen_random_uuid(), c."Id", c."TenantId", 0, true, false, 'Gestión general',
                       (SELECT p."Id" FROM "ProveedoresPlataformaCae" p WHERE p."Activo" ORDER BY p."Codigo" LIMIT 1),
                       now(), gen_random_uuid()
                FROM "Centros" c WHERE c."Id" = @centro::uuid
                RETURNING "Id", "TenantId")
            INSERT INTO "AcreditacionesDocumentoPlataforma"
                ("Id", "CanalGestionDocumentalId", "DocumentoId", "TenantId", "Estado", "EstadoVigencia",
                 "EstaEliminado", "CreadoEnUtc", "Version")
            SELECT gen_random_uuid(), canal."Id", d."Id", canal."TenantId", 0, 0, false, now(), gen_random_uuid()
            FROM canal
            JOIN "Documentos" d ON d."TenantId" = canal."TenantId" AND NOT d."EstaEliminado"
            JOIN "Trabajadores" t ON t."Id" = d."TrabajadorId" AND t."Apellidos" = @apellidos
            """, ("centro", centroId), ("apellidos", apellidosTrabajador));
        Assert.Equal(1, acreditaciones);

        // --- /centros: el motivo «1 pendiente en la plataforma», con su ventana ---
        await Ayudas.NavegarYEsperarAsync(page, urlLista);
        await Expect(cabeceraFila.Locator(".motivo-recuento-pendientes"))
            .ToHaveTextAsync("1 pendiente en la plataforma", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
        // La pastilla dice «Pendiente» (con mayúscula; el motivo, en minúscula): el Trabajador tiene además otros tipos que
        // faltan, y Faltante se rotula igual.
        await Expect(cabeceraFila.Locator(".ranura-estado-centro")).ToContainTextAsync("Pendiente");
        var ventana = cabeceraFila.Locator(".ventana-contexto").Filter(new LocatorFilterOptions { Has = page.Locator(".motivo-recuento-pendientes") });
        await Expect(ventana).ToHaveAttributeAsync("aria-label", "1 pendiente en la plataforma. 1 documento pendiente en la plataforma");
        await Expect(ventana.Locator(".ventana-linea")).ToHaveTextAsync(
            new System.Text.RegularExpressions.Regex($"— {nombreTrabajador} {apellidosTrabajador} — sin subir a la plataforma$"));

        // --- Ficha 360 del Centro: el mismo recuento junto al título ---
        await Ayudas.NavegarYEsperarAsync(page, $"{fixture.BaseUrl}/centros/{centroId}");
        await Expect(page.Locator("[data-recuento='pendientes-plataforma']"))
            .ToHaveTextAsync("1 pendiente en la plataforma", new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
    }
}
