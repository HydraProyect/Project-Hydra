using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CaeManager.E2ETests;

/// <summary>
/// Pendiente en la plataforma CAE del Centro (decisión del 2026-10-10), de punta a punta. Un Centro sin plataforma no tiene
/// ese motivo ni bloquea a nadie; en cuanto los documentos vigentes de su Trabajador quedan pendientes en la plataforma del
/// Centro, la fila de /centros y la ficha 360 NOMBRAN el documento y lo que le falta, y el Trabajador queda bloqueado en el
/// Centro <b>solo por el tipo que el Centro marca «bloquea el acceso»</b>: el pendiente de un tipo incluido sin esa marca (la
/// «ISO opcional») pone el Centro en «Pendiente» pero no bloquea. Lo que bUnit no ve: que la consulta real (RLS, alcance,
/// documento efectivo, canal del Centro, marca de bloqueo) llega a esas dos pantallas.
///
/// <para>
/// El escenario es propio y se escribe por SQL en el Tenant de Refrielectric, el de la Gestora CAE con la que se entra (la
/// misma que <see cref="BucleCorreccionPlataformaTests"/>): un Centro nuevo (del primer Cliente empresarial y la primera
/// Empresa ya sembrados en ese Tenant), un Trabajador nuevo de esa Empresa asignado a él y sus documentos vigentes de los dos
/// primeros tipos de Trabajador que se piden siempre; el Centro marca el primero como bloqueante y el segundo solo como
/// incluido. Después, el acceso de plataforma del Centro y las dos acreditaciones: la del tipo bloqueante sin subir, la del
/// otro subida sin validar. Darlos de alta por la interfaz no es lo que se mide, y escribirlos así no dispara ningún envío:
/// no se envía nada. Ningún otro E2E lee este Centro ni este Trabajador.
/// </para>
/// </summary>
[Collection("AppCollection")]
public class CentroPendienteEnPlataformaE2ETests(WebAppFixture fixture)
{
    private const string TiposDeTrabajadorQueSePidenSiempre = """
        SELECT t."Id"::text FROM "TiposDocumento" t JOIN "Tenants" n ON n."Id" = t."TenantId"
        WHERE n."Nombre" = @tenant AND t."Requerido" = 'Si' AND t."AmbitoAplicacion" = 'Trabajador'
        ORDER BY t."Orden", t."Nombre", t."Id"
        """;

    [Fact]
    public async Task Un_tipo_bloqueante_pendiente_bloquea_al_Trabajador_y_lo_nombra_y_uno_opcional_no_bloquea()
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var nombreCentro = $"EnPlataforma Centro {sufijo}";
        const string nombreTrabajador = "EnPlataforma";
        var apellidosTrabajador = $"E2E {sufijo}";
        var trabajador = $"{nombreTrabajador} {apellidosTrabajador}";
        var hoy = Ayudas.HoyDeNegocio();

        var tipoBloqueante = await fixture.LeerValorSqlAsync($"{TiposDeTrabajadorQueSePidenSiempre} LIMIT 1", ("tenant", Ayudas.NombreTenantRefrielectric));
        var tipoOpcional = await fixture.LeerValorSqlAsync($"{TiposDeTrabajadorQueSePidenSiempre} OFFSET 1 LIMIT 1", ("tenant", Ayudas.NombreTenantRefrielectric));
        Assert.False(string.IsNullOrEmpty(tipoBloqueante) || string.IsNullOrEmpty(tipoOpcional) || tipoBloqueante == tipoOpcional,
            $"Hacen falta dos tipos de Trabajador que se piden siempre en el Tenant: '{tipoBloqueante}', '{tipoOpcional}'");
        var nombreTipoBloqueante = await fixture.LeerValorSqlAsync("""SELECT "Nombre" FROM "TiposDocumento" WHERE "Id" = @id::uuid""", ("id", tipoBloqueante));
        var nombreTipoOpcional = await fixture.LeerValorSqlAsync("""SELECT "Nombre" FROM "TiposDocumento" WHERE "Id" = @id::uuid""", ("id", tipoOpcional));

        // --- Centro sin plataforma, con un Trabajador asignado y sus dos documentos vigentes ---
        // EstadoVigencia 2 = VenceEnFecha; GestionCae 0 = con gestión.
        var documentos = await fixture.EjecutarSqlAsync("""
            WITH tenant AS (
                SELECT "Id" FROM "Tenants" WHERE "Nombre" = @tenant),
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
            marcas AS (
                INSERT INTO "TiposDocumentoCentros" ("Id", "TipoDocumentoId", "CentroId", "TenantId", "Incluido", "BloqueaAcceso")
                SELECT gen_random_uuid(), m.tipo, centro."Id", centro."TenantId", true, m.bloquea
                FROM centro, (VALUES (@bloqueante::uuid, true), (@opcional::uuid, false)) AS m(tipo, bloquea)
                RETURNING 1)
            INSERT INTO "Documentos"
                ("Id", "TenantId", "TipoDocumentoId", "TrabajadorId", "FechaEmision", "FechaVencimiento", "EstadoVigencia",
                 "EstaEliminado", "CreadoEnUtc", "Version")
            SELECT gen_random_uuid(), trabajador."TenantId", tipo.id, trabajador."Id", @hoy::date - 5, @hoy::date + 180, 2,
                   false, now(), gen_random_uuid()
            FROM trabajador, (VALUES (@bloqueante::uuid), (@opcional::uuid)) AS tipo(id),
                 (SELECT count(*) FROM asignacion) AS asignada, (SELECT count(*) FROM marcas) AS marcadas
            """,
            ("tenant", Ayudas.NombreTenantRefrielectric), ("centro", nombreCentro), ("nombre", nombreTrabajador),
            ("apellidos", apellidosTrabajador), ("dni", Ayudas.GenerarDniValido(88_100_981)), ("hoy", hoy.ToString("yyyy-MM-dd")),
            ("bloqueante", tipoBloqueante), ("opcional", tipoOpcional));
        Assert.Equal(2, documentos);
        var centroId = await fixture.LeerValorSqlAsync("""SELECT "Id"::text FROM "Centros" WHERE "Nombre" = @centro""", ("centro", nombreCentro));

        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailGestorRefrielectric, Ayudas.ContrasenaUsuariosPrueba);

        var urlLista = $"{fixture.BaseUrl}/centros?q={Uri.EscapeDataString(nombreCentro)}";
        var urlFicha = $"{fixture.BaseUrl}/centros/{centroId}";
        var filaCentro = page.Locator(".tarjeta-fila-acordeon").Filter(new LocatorFilterOptions
        {
            Has = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Asignaciones de {nombreCentro}", Exact = true }),
        });
        var cabeceraFila = filaCentro.Locator(".tarjeta-fila-acordeon-cabecera");
        var pendientesFicha = page.Locator("[data-recuento='pendientes-plataforma']");
        // La ventana de los Trabajadores bloqueados de la ficha (CentroDetalle): no interactiva, su nombre es su título.
        var ventanaBloqueados = page.Locator(".ventana-contexto[aria-label='1 trabajador bloqueado']");
        var lineasBloqueo = ventanaBloqueados.Locator(".ventana-linea");

        // --- Sin plataforma: ni motivo pendiente ni Trabajador bloqueado (controles negativos, con barrera) ---
        await Ayudas.NavegarYEsperarAsync(page, urlLista);
        await Expect(cabeceraFila.Locator(".ranura-estado-centro")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Expect(cabeceraFila.Locator(".motivo-recuento-pendientes")).ToHaveCountAsync(0);
        await Ayudas.NavegarYEsperarAsync(page, urlFicha);
        await Expect(page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = nombreCentro }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        // Barrera: el resumen del Centro ya está pintado junto al título antes de mirar lo que no debe estar.
        await Expect(page.Locator(".centro360-indicadores")).ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = 15_000 });
        await Expect(pendientesFicha).ToHaveCountAsync(0);
        await Expect(page.Locator(".ventana-contexto[aria-label$='bloqueado'], .ventana-contexto[aria-label$='bloqueados']")).ToHaveCountAsync(0);

        // --- El Centro pasa a tener plataforma: el tipo bloqueante sin subir, el opcional subido sin validar ---
        // Tipo 0 = TipoCanalGestion.Plataforma; Estado 0 = PendienteDeSubir y 1 = Subida; EstadoVigencia 0 = SinConfirmar.
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
            SELECT gen_random_uuid(), canal."Id", d."Id", canal."TenantId",
                   CASE WHEN d."TipoDocumentoId" = @bloqueante::uuid THEN 0 ELSE 1 END, 0, false, now(), gen_random_uuid()
            FROM canal
            JOIN "Documentos" d ON d."TenantId" = canal."TenantId" AND NOT d."EstaEliminado"
            JOIN "Trabajadores" t ON t."Id" = d."TrabajadorId" AND t."Apellidos" = @apellidos
            """, ("centro", centroId), ("apellidos", apellidosTrabajador), ("bloqueante", tipoBloqueante));
        Assert.Equal(2, acreditaciones);

        // --- /centros: el motivo NOMBRA el documento y lo que le falta, no solo cuenta ---
        // Sin subir va antes que subido (lo que más falta), así que el nombrado es el del tipo bloqueante.
        var motivo = $"{nombreTipoBloqueante} — {trabajador} — sin subir a la plataforma y 1 más";
        await Ayudas.NavegarYEsperarAsync(page, urlLista);
        await Expect(cabeceraFila.Locator(".motivo-recuento-pendientes"))
            .ToHaveTextAsync(motivo, new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
        await Expect(cabeceraFila.Locator(".ranura-estado-centro")).ToContainTextAsync("Pendiente");
        var ventana = cabeceraFila.Locator(".ventana-contexto").Filter(new LocatorFilterOptions { Has = page.Locator(".motivo-recuento-pendientes") });
        await Expect(ventana).ToHaveAttributeAsync("aria-label", $"{motivo}. 2 documentos pendientes en la plataforma");
        await Expect(ventana.Locator(".ventana-linea")).ToHaveTextAsync(
        [
            $"{nombreTipoBloqueante} — {trabajador} — sin subir a la plataforma",
            $"{nombreTipoOpcional} — {trabajador} — subido, sin validar en la plataforma",
        ]);

        // --- Ficha 360: el mismo motivo, y el Trabajador bloqueado SOLO por el tipo bloqueante ---
        await Ayudas.NavegarYEsperarAsync(page, urlFicha);
        await Expect(pendientesFicha).ToHaveTextAsync(motivo, new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
        await Expect(ventanaBloqueados).ToBeVisibleAsync();
        await Expect(lineasBloqueo).ToHaveTextAsync(
            [$"{trabajador} · {nombreTipoBloqueante}: sin subir a la plataforma del Centro"],
            new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
    }
}
