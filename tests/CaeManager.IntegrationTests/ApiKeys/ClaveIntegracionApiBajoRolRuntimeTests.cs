using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.ApiKeys;

/// <summary>
/// La Clave de Integración del Tenant por HTTP, bajo <c>cae_app_runtime</c>: canje del secreto, token,
/// alcances, proyección y cortes. Mismo arnés que <see cref="ApiPublicaBajoRolRuntimeTests"/> y con sus mismos
/// límites, más una sesión de cookie simulada como esquema por defecto: en <c>Program.cs</c> la cookie es el
/// esquema por defecto y la política tiene que dejarla fuera. El host vive todo el test, porque lo que se mide
/// es qué le pasa a un token ya emitido cuando cambia el estado.
/// </summary>
public class ClaveIntegracionApiBajoRolRuntimeTests : IAsyncLifetime
{
    private const string Secreto = $"{ClaveIntegracionTenant.PrefijoSecreto}0123456789abcdef0123456789abcdef";
    private const string CabeceraCookieSimulada = "X-Cookie-Simulada";
    private const string DniDelTrabajador = "12345678Z";

    private static readonly string[] TodosLosAlcancesDeLectura =
    [
        AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.TrabajadoresLeer, AlcancesIntegracion.DocumentosLeer,
    ];

    private readonly string _cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _tenantOperador;
    private Guid _claveId;
    private Guid _trabajadorA;
    private Guid _trabajadorB;
    private Guid _documentoGeneral;
    private Guid _documentoSalud;
    private Guid _usuarioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContextoPropietario();
        await contexto.Database.MigrateAsync();

        var tenantA = new Tenant("Organización Alfa");
        var tenantB = new Tenant("Organización Beta");
        var operador = new Tenant("Operador CAE externo Gamma");
        tenantA.CambiarIntegracionHabilitada(true);
        tenantB.CambiarIntegracionHabilitada(true);
        operador.CambiarIntegracionHabilitada(true);
        contexto.Tenants.AddRange(tenantA, tenantB, operador);
        _usuarioId = Guid.NewGuid();
        contexto.Users.Add(new ApplicationUser { Id = _usuarioId, TenantId = tenantA.Id, UserName = "emisor@ejemplo.test", Email = "emisor@ejemplo.test" });
        contexto.DelegacionesTenant.Add(new DelegacionTenant(operador.Id, tenantB.Id));
        await contexto.SaveChangesAsync();
        (_tenantA, _tenantB, _tenantOperador) = (tenantA.Id, tenantB.Id, operador.Id);

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var empresa = new Empresa("Empresa contratista Alfa", "B10380194");
            contexto.Empresas.Add(empresa);
            var general = new TipoDocumento("Formación PRL", null, false, 1, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
            var salud = new TipoDocumento("Aptitud médica", null, false, 2, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
            contexto.TiposDocumento.AddRange(general, salud);
            contexto.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Perez", DniDelTrabajador);
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            _trabajadorA = trabajador.Id;

            var hoy = DiaDeNegocio.Hoy();
            var documentoGeneral = Documento.DeTrabajador(trabajador.Id, general.Id, hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, "a/general.pdf");
            var documentoSalud = Documento.DeTrabajador(trabajador.Id, salud.Id, hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, "a/salud.pdf");
            contexto.Documentos.AddRange(documentoGeneral, documentoSalud);
            await contexto.SaveChangesAsync();
            (_documentoGeneral, _documentoSalud) = (documentoGeneral.Id, documentoSalud.Id);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var empresa = new Empresa("Empresa contratista Beta", "B10380202");
            contexto.Empresas.Add(empresa);
            contexto.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));
            await contexto.SaveChangesAsync();
            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Luis", "Garcia", "00000000T");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            _trabajadorB = trabajador.Id;
        }

        _claveId = await EmitirClaveAsync(_tenantA, Secreto, [.. TodosLosAlcancesDeLectura, AlcancesIntegracion.TrabajadoresDatosPersonalesLeer]);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaPropietario);

    [Fact]
    public async Task El_secreto_se_canjea_por_un_token_de_15_minutos_que_lee_y_deja_constancia_del_ultimo_uso()
    {
        await using var api = await Api.IniciarAsync(this);

        var respuesta = await api.CanjearAsync(_claveId, Secreto);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK, await api.DetalleAsync(respuesta));
        respuesta.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("token_type").GetString().Should().Be("Bearer");
        json.RootElement.GetProperty("expires_in").GetInt32().Should().Be(900);
        json.RootElement.GetProperty("access_token").GetString().Should().NotContain(Secreto);

        var (estado, cuerpo) = await api.GetAsync(json.RootElement.GetProperty("access_token").GetString()!, $"/api/v1/trabajadores/{_trabajadorA}");
        estado.Should().Be(HttpStatusCode.OK, cuerpo);
        cuerpo.Should().Contain(DniDelTrabajador, "control positivo: esta clave tiene el alcance de datos personales");

        await using var contexto = CrearContextoPropietario();
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        (await contexto.ClavesIntegracionTenant.SingleAsync(c => c.Id == _claveId)).UltimoUsoUtc
            .Should().NotBeNull("EF escribe el último uso bajo cae_app_runtime, que solo tiene permiso por columnas");
    }

    [Fact]
    public async Task Cada_lectura_autorizada_suma_a_la_fila_del_dia_por_patron_de_ruta_y_lo_denegado_no_suma()
    {
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(_claveId, Secreto);

        (await api.GetAsync(token, $"/api/v1/trabajadores/{_trabajadorA}")).Estado.Should().Be(HttpStatusCode.OK);
        (await api.GetAsync(token, $"/api/v1/trabajadores/{_trabajadorA}")).Estado.Should().Be(HttpStatusCode.OK);
        (await api.GetAsync(token, "/api/v1/asignaciones")).Estado.Should().Be(HttpStatusCode.Forbidden, "control: esta clave no tiene ese alcance");

        await using var contexto = CrearContextoPropietario();
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        var fila = (await contexto.UsosApiDiarios.ToListAsync()).Should().ContainSingle().Subject;
        fila.ApiKeyId.Should().Be(_claveId);
        fila.TipoClave.Should().Be(TipoClaveUsoApi.ClaveIntegracion);
        fila.Dia.Should().Be(DiaDeNegocio.Hoy());
        fila.Endpoint.Should().Be("/api/v1/trabajadores/{id:guid}", "el patrón, sin el identificador de la URL");
        fila.Peticiones.Should().Be(2);
    }

    [Theory]
    [InlineData("secreto de otra clave", HttpStatusCode.Unauthorized)]
    [InlineData("Id de otra clave", HttpStatusCode.Unauthorized)]
    [InlineData("sin credenciales", HttpStatusCode.Unauthorized)]
    [InlineData("grant distinto", HttpStatusCode.BadRequest)]
    public async Task Un_canje_invalido_no_da_token_ni_dice_por_que(string caso, HttpStatusCode esperado)
    {
        await using var api = await Api.IniciarAsync(this);

        var respuesta = caso switch
        {
            "secreto de otra clave" => await api.CanjearAsync(_claveId, $"{ClaveIntegracionTenant.PrefijoSecreto}otro"),
            "Id de otra clave" => await api.CanjearAsync(Guid.NewGuid(), Secreto),
            "sin credenciales" => await api.CanjearAsync(null, null),
            _ => await api.CanjearAsync(_claveId, Secreto, "password"),
        };

        respuesta.StatusCode.Should().Be(esperado, await api.DetalleAsync(respuesta));
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        cuerpo.Should().NotContain("access_token");
        if (esperado == HttpStatusCode.Unauthorized)
            cuerpo.Should().Be("""{"error":"invalid_client"}""");
    }

    [Fact]
    public async Task El_secreto_no_sirve_como_Bearer_ni_el_token_como_ApiKey()
    {
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(_claveId, Secreto);

        (await api.GetAsync(Secreto, "/api/v1/clientes")).Estado.Should().Be(HttpStatusCode.Unauthorized);
        (await api.GetConCabeceraAsync(new AuthenticationHeaderValue("ApiKey", token), "/api/v1/clientes")).Estado.Should().Be(HttpStatusCode.Unauthorized);
        (await api.GetConCabeceraAsync(new AuthenticationHeaderValue("ApiKey", Secreto), "/api/v1/clientes")).Estado.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Una_sesion_de_cookie_sin_clave_no_entra()
    {
        await using var api = await Api.IniciarAsync(this);

        var (estado, cuerpo) = await api.GetConCookieAsync("/api/v1/clientes");

        estado.Should().Be(HttpStatusCode.Unauthorized, cuerpo);
    }

    [Fact]
    public async Task La_clave_de_un_Tenant_no_alcanza_el_recurso_de_otro()
    {
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(_claveId, Secreto);

        (await api.GetAsync(token, $"/api/v1/trabajadores/{_trabajadorA}")).Estado.Should().Be(HttpStatusCode.OK, "control positivo");
        (await api.GetAsync(token, $"/api/v1/trabajadores/{_trabajadorB}")).Estado.Should().Be(HttpStatusCode.NotFound);
    }

    // La delegación da operación a personas del Operador CAE externo, no a su clave: la clave ve su Tenant propietario.
    [Fact]
    public async Task La_clave_de_un_Operador_CAE_externo_no_alcanza_a_su_Tenant_beneficiario()
    {
        const string secretoOperador = $"{ClaveIntegracionTenant.PrefijoSecreto}operador0123456789abcdef";
        var claveOperador = await EmitirClaveAsync(_tenantOperador, secretoOperador, TodosLosAlcancesDeLectura);
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(claveOperador, secretoOperador);

        (await api.GetAsync(token, $"/api/v1/trabajadores/{_trabajadorB}")).Estado.Should().Be(HttpStatusCode.NotFound);
        var (estado, cuerpo) = await api.GetAsync(token, "/api/v1/trabajadores");
        estado.Should().Be(HttpStatusCode.OK, cuerpo);
        cuerpo.Should().NotContain("Garcia");
    }

    [Fact]
    public async Task Sin_el_alcance_de_la_ruta_responde_403_sin_datos_del_recurso()
    {
        const string secreto = $"{ClaveIntegracionTenant.PrefijoSecreto}solo-clientes0123456789ab";
        var clave = await EmitirClaveAsync(_tenantA, secreto, [AlcancesIntegracion.ClientesLeer]);
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(clave, secreto);

        (await api.GetAsync(token, "/api/v1/clientes")).Estado.Should().Be(HttpStatusCode.OK, "control positivo");
        var (estado, cuerpo) = await api.GetAsync(token, "/api/v1/trabajadores");
        estado.Should().Be(HttpStatusCode.Forbidden);
        cuerpo.Should().NotContain("Perez");
        (await api.GetAsync(token, $"/api/v1/documentos/{_documentoGeneral}")).Estado.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Con_el_alcance_de_asignaciones_recibe_la_proyeccion_publica_y_queda_en_el_rastro()
    {
        await using (var contexto = CrearContextoPropietario())
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var empresa = await contexto.Empresas.SingleAsync();
            var cliente = Empresa.CrearComoCliente("Cliente empresarial Alfa", "B12345674", false, null, null);
            contexto.Empresas.Add(cliente);
            var centro = new Centro(cliente.Id, empresa.Id, "Planta Alfa");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();
            contexto.Asignaciones.Add(new Asignacion(_trabajadorA, centro.Id, DiaDeNegocio.Hoy()));
            await contexto.SaveChangesAsync();
        }

        const string secreto = $"{ClaveIntegracionTenant.PrefijoSecreto}solo-asignaciones0123456789";
        var clave = await EmitirClaveAsync(_tenantA, secreto, [AlcancesIntegracion.AsignacionesLeer]);
        await using var api = await Api.IniciarAsync(this);

        var (estado, cuerpo) = await api.GetAsync(await api.TokenAsync(clave, secreto), "/api/v1/asignaciones");

        estado.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(cuerpo);
        var fila = json.RootElement.GetProperty("elementos").EnumerateArray().Should().ContainSingle().Subject;
        fila.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "trabajadorId", "trabajadorNombre", "centroId", "centroNombre", "clienteId", "clienteNombre", "fechaAlta", "fechaBaja");

        await using var lectura = CrearContextoPropietario();
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        (await lectura.UsosApiDiarios.SingleAsync(u => u.ApiKeyId == clave)).Endpoint.Should().Be("/api/v1/asignaciones");
    }

    [Fact]
    public async Task Sin_el_alcance_de_datos_personales_el_DNI_ni_se_devuelve_ni_se_puede_buscar()
    {
        const string secreto = $"{ClaveIntegracionTenant.PrefijoSecreto}sin-datos0123456789abcdef";
        var clave = await EmitirClaveAsync(_tenantA, secreto, [AlcancesIntegracion.TrabajadoresLeer]);
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(clave, secreto);
        var tokenConAlcance = await api.TokenAsync(_claveId, Secreto);

        var lista = await api.GetAsync(token, "/api/v1/trabajadores");
        lista.Estado.Should().Be(HttpStatusCode.OK, lista.Cuerpo);
        lista.Cuerpo.Should().Contain("Perez", "control positivo").And.NotContain(DniDelTrabajador).And.NotContain("\"dni\"")
            .And.NotContain("estadoDocumental", "el estado se calcula también con los documentos de salud");

        var detalle = await api.GetAsync(token, $"/api/v1/trabajadores/{_trabajadorA}");
        detalle.Cuerpo.Should().Contain("Perez", "control positivo").And.NotContain(DniDelTrabajador).And.NotContain("\"dni\"");

        (await api.GetAsync(tokenConAlcance, $"/api/v1/trabajadores?busqueda={DniDelTrabajador}")).Cuerpo
            .Should().Contain("Perez", "control positivo: con el alcance el DNI se busca");
        (await api.GetAsync(token, $"/api/v1/trabajadores?busqueda={DniDelTrabajador}")).Cuerpo
            .Should().NotContain("Perez", "buscar por DNI confirmaría a quién pertenece");
    }

    [Fact]
    public async Task Ni_con_el_alcance_de_datos_personales_llega_el_estado_documental_que_delataria_un_documento_de_salud()
    {
        await using var api = await Api.IniciarAsync(this);

        var (estado, cuerpo) = await api.GetAsync(await api.TokenAsync(_claveId, Secreto), "/api/v1/trabajadores");

        estado.Should().Be(HttpStatusCode.OK, cuerpo);
        cuerpo.Should().Contain(DniDelTrabajador, "control positivo").And.NotContain("estadoDocumental");
        using var json = JsonDocument.Parse(cuerpo);
        if (json.RootElement.TryGetProperty("recuentosPorEstado", out var recuentos))
            recuentos.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Las_listas_de_Clientes_empresariales_y_Centros_no_traen_estado_que_delate_un_documento_de_salud()
    {
        await using (var contexto = CrearContextoPropietario())
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial Alfa", "B12345674", false, null, null);
            var propia = new Empresa("Empresa propia Alfa", "P1234567D");
            contexto.Empresas.AddRange(cliente, propia);
            await contexto.SaveChangesAsync();
            contexto.Centros.Add(new Centro(cliente.Id, propia.Id, "Centro Alfa"));
            await contexto.SaveChangesAsync();
        }

        const string secreto = $"{ClaveIntegracionTenant.PrefijoSecreto}listas0123456789abcdef";
        var clave = await EmitirClaveAsync(_tenantA, secreto, [AlcancesIntegracion.ClientesLeer, AlcancesIntegracion.CentrosLeer]);
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(clave, secreto);

        var clientes = await api.GetAsync(token, "/api/v1/clientes");
        clientes.Estado.Should().Be(HttpStatusCode.OK, clientes.Cuerpo);
        clientes.Cuerpo.Should().Contain("Cliente empresarial Alfa", "control positivo").And.NotContain("estadoDocumental");

        var centros = await api.GetAsync(token, "/api/v1/centros");
        centros.Estado.Should().Be(HttpStatusCode.OK, centros.Cuerpo);
        centros.Cuerpo.Should().Contain("Centro Alfa", "control positivo")
            .And.NotContain("\"estado\"").And.NotContain("cumplimientoPorcentaje").And.NotContain("recuentos\"");
    }

    [Fact]
    public async Task Un_documento_de_salud_no_existe_para_la_clave_ni_en_la_lista_ni_en_el_total_ni_en_el_detalle()
    {
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(_claveId, Secreto);

        var lista = await api.GetAsync(token, "/api/v1/documentos");

        lista.Estado.Should().Be(HttpStatusCode.OK, lista.Cuerpo);
        using var json = JsonDocument.Parse(lista.Cuerpo);
        json.RootElement.GetProperty("totalElementos").GetInt32().Should().Be(1, "el general sí se ve y el de salud no cuenta");
        lista.Cuerpo.Should().Contain(_documentoGeneral.ToString()).And.NotContain(_documentoSalud.ToString());
        (await api.GetAsync(token, $"/api/v1/documentos/{_documentoGeneral}")).Estado.Should().Be(HttpStatusCode.OK, "control positivo");
        (await api.GetAsync(token, $"/api/v1/documentos/{_documentoSalud}")).Estado.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// § 7 de la propuesta: el token ya emitido cae en la petición siguiente a cada corte.
    /// </summary>
    [Theory]
    [InlineData("clave caducada", HttpStatusCode.Unauthorized)]
    [InlineData("clave revocada", HttpStatusCode.Unauthorized)]
    [InlineData("clave invalidada", HttpStatusCode.Unauthorized)]
    [InlineData("clave suspendida", HttpStatusCode.Unauthorized)]
    [InlineData("Tenant suspendido", HttpStatusCode.Unauthorized)]
    [InlineData("Tenant reactivado", HttpStatusCode.Unauthorized)]
    [InlineData("comercial suspendida", HttpStatusCode.Unauthorized)]
    [InlineData("solo lectura", HttpStatusCode.Unauthorized)]
    [InlineData("integracion deshabilitada", HttpStatusCode.Unauthorized)]
    [InlineData("emisor dado de baja", HttpStatusCode.OK)]
    public async Task Cada_corte_alcanza_al_token_ya_emitido_y_al_canje(string caso, HttpStatusCode esperado)
    {
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(_claveId, Secreto);
        (await api.GetAsync(token, "/api/v1/clientes")).Estado.Should().Be(HttpStatusCode.OK, "control positivo: antes del corte vale");

        await AplicarCorteAsync(caso);

        var (estado, cuerpo) = await api.GetAsync(token, "/api/v1/clientes");
        estado.Should().Be(esperado, "caso {0}. Detalle: {1}", caso, cuerpo);
        (await api.CanjearAsync(_claveId, Secreto)).StatusCode
            .Should().Be(esperado, "el canje aplica los mismos cortes. Caso {0}", caso);
    }

    /// <summary>El corte se guarda y se observa antes de revertirlo: si no, el test solo vería el estado final.</summary>
    [Theory]
    [InlineData("clave suspendida")]
    [InlineData("comercial suspendida")]
    [InlineData("solo lectura")]
    [InlineData("integracion deshabilitada")]
    public async Task Un_corte_reversible_devuelve_el_acceso_al_mismo_token_y_a_la_misma_clave(string caso)
    {
        await using var api = await Api.IniciarAsync(this);
        var token = await api.TokenAsync(_claveId, Secreto);
        await AplicarCorteAsync(caso);
        (await api.GetAsync(token, "/api/v1/clientes")).Estado.Should().Be(HttpStatusCode.Unauthorized, "control: el corte está aplicado");

        await RevertirCorteAsync(caso);

        var (estado, cuerpo) = await api.GetAsync(token, "/api/v1/clientes");
        estado.Should().Be(HttpStatusCode.OK, "caso {0}. Detalle: {1}", caso, cuerpo);
        (await api.CanjearAsync(_claveId, Secreto)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task RevertirCorteAsync(string caso)
    {
        await using var contexto = CrearContextoPropietario();
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantA);

        if (caso == "clave suspendida")
            (await contexto.ClavesIntegracionTenant.SingleAsync(c => c.Id == _claveId)).Reactivar();
        else if (caso == "integracion deshabilitada")
            tenant.CambiarIntegracionHabilitada(true);
        else
            tenant.ActualizarEstadoComercial(EstadoComercialTenant.Activa);
        await contexto.SaveChangesAsync();
    }

    private async Task AplicarCorteAsync(string caso)
    {
        await using var contexto = CrearContextoPropietario();
        using var ambito = AmbitoTenantExplicito.Establecer(_tenantA);
        var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantA);
        var clave = await contexto.ClavesIntegracionTenant.SingleAsync(c => c.Id == _claveId);

        switch (caso)
        {
            case "clave caducada":
                contexto.Entry(clave).Property(c => c.CaducaEnUtc).CurrentValue = DateTime.UtcNow.AddDays(-1);
                break;
            case "clave revocada":
                clave.Revocar();
                break;
            case "clave invalidada":
                clave.InvalidarDefinitivamente();
                break;
            case "clave suspendida":
                clave.Suspender("Incidente");
                break;
            case "Tenant suspendido":
                tenant.Suspender();
                break;
            case "Tenant reactivado":
                tenant.Suspender();
                tenant.Reactivar();
                break;
            case "comercial suspendida":
                tenant.VincularSuscripcionStripe("cus_prueba", "sub_prueba", EstadoComercialTenant.Activa);
                tenant.ActualizarEstadoComercial(EstadoComercialTenant.Suspendida);
                break;
            case "solo lectura":
                tenant.VincularSuscripcionStripe("cus_prueba", "sub_prueba", EstadoComercialTenant.Activa);
                tenant.ActualizarEstadoComercial(EstadoComercialTenant.SoloLectura);
                break;
            case "integracion deshabilitada":
                tenant.CambiarIntegracionHabilitada(false);
                break;
            case "emisor dado de baja":
                (await contexto.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _usuarioId)).Desactivar();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caso), caso, null);
        }
        await contexto.SaveChangesAsync();
    }

    private async Task<Guid> EmitirClaveAsync(Guid tenantId, string secreto, IEnumerable<string> alcances)
    {
        await using var contexto = CrearContextoPropietario();
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var clave = new ClaveIntegracionTenant("ERP de prueba", "ERP", secreto[..ClaveIntegracionTenant.PrefijoSecreto.Length],
            CabeceraClaveApi.HashDeClave(secreto), alcances, DateTime.UtcNow.AddDays(30), _usuarioId);
        contexto.ClavesIntegracionTenant.Add(clave);
        await contexto.SaveChangesAsync();
        return clave.Id;
    }

    private CaeManagerDbContext CrearContextoPropietario()
    {
        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class Api : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _http;
        private readonly List<string> _excepciones;

        private Api(WebApplication app, HttpClient http, List<string> excepciones)
        {
            _app = app;
            _http = http;
            _excepciones = excepciones;
        }

        public static async Task<Api> IniciarAsync(ClaveIntegracionApiBajoRolRuntimeTests prueba)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CaeManagerDb"] = prueba._cadenaPropietario,
                ["ConnectionStrings:CaeManagerDbRuntime"] = BaseDatosPostgresDePruebas.CadenaComoRuntime(prueba._cadenaPropietario),
                ["DataProtection:PermitirClavesSinCifrar"] = "true",
            });

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

            foreach (var descriptor in builder.Services
                         .Where(s => s.ServiceType == typeof(IHostedService)
                             && s.ImplementationType?.Assembly == typeof(InfrastructureServiceCollectionExtensions).Assembly)
                         .ToList())
            {
                builder.Services.Remove(descriptor);
            }

            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            builder.Services.AddScoped<AuthenticationStateProvider, SinCircuitoDeBlazor>();
            builder.Services.AddScoped<IClienteActivoSeleccionado, SinClienteActivo>();
            builder.Services.AddScoped<ICurrentUserService, CaeManager.Web.Services.CurrentUserService>();
            builder.Services.AddScoped<IActorAuditoria, CaeManager.Web.Services.ActorAuditoriaDesdeSesion>();
            builder.Services.AddScoped<ITenantActual, CaeManager.Web.Services.TenantActual>();

            builder.Services
                .AddAuthentication(CookieSimuladaHandler.Esquema)
                .AddScheme<AuthenticationSchemeOptions, CookieSimuladaHandler>(CookieSimuladaHandler.Esquema, _ => { })
                .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationSchemeOptions.NombreEsquema, _ => { })
                .AddScheme<ClaveIntegracionAuthenticationSchemeOptions, ClaveIntegracionAuthenticationHandler>(ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema, _ => { });

            builder.Services.AddAuthorizationBuilder()
                .AddPolicy("ApiPublica", policy => policy
                    .AddAuthenticationSchemes(ApiKeyAuthenticationSchemeOptions.NombreEsquema, ClaveIntegracionAuthenticationSchemeOptions.NombreEsquema)
                    .RequireAuthenticatedUser());
            builder.Services.AddRateLimiter(opciones => opciones.AddPolicy(CaeManager.Web.Services.LimitadorApiPublica.PoliticaToken,
                contexto => CaeManager.Web.Services.LimitadorApiPublica.ParticionToken(contexto, 1000)));

            var app = builder.Build();

            var excepciones = new List<string>();
            app.Use(async (contexto, siguiente) =>
            {
                try
                {
                    await siguiente(contexto);
                }
                catch (Exception ex)
                {
                    excepciones.Add(ex.ToString());
                    throw;
                }
            });

            app.UseAuthentication();
            app.UseAuthorization();
            app.UseRateLimiter();

            app.MapGroup("/api/v1")
                .RequireAuthorization("ApiPublica")
                .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>()
                .AddEndpointFilter<AlcanceIntegracionEndpointFilter>()
                .AddEndpointFilter<RastroUsoApiEndpointFilter>()
                .MapRecursosApiV1();
            app.MapTokenIntegracionEndpoint();

            await app.StartAsync();
            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Api(app, new HttpClient { BaseAddress = new Uri(direccion) }, excepciones);
        }

        public async Task<HttpResponseMessage> CanjearAsync(Guid? claveId, string? secreto, string tipoConcesion = "client_credentials")
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/v1/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = tipoConcesion }),
            };
            if (claveId is not null)
                peticion.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{claveId}:{secreto}")));
            return await _http.SendAsync(peticion);
        }

        public async Task<string> TokenAsync(Guid claveId, string secreto)
        {
            var respuesta = await CanjearAsync(claveId, secreto);
            respuesta.StatusCode.Should().Be(HttpStatusCode.OK, await DetalleAsync(respuesta));
            using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("access_token").GetString()!;
        }

        public Task<(HttpStatusCode Estado, string Cuerpo)> GetAsync(string token, string ruta) =>
            GetConCabeceraAsync(new AuthenticationHeaderValue("Bearer", token), ruta);

        public async Task<(HttpStatusCode Estado, string Cuerpo)> GetConCabeceraAsync(AuthenticationHeaderValue cabecera, string ruta)
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
            peticion.Headers.Authorization = cabecera;
            return await EnviarAsync(peticion);
        }

        public async Task<(HttpStatusCode Estado, string Cuerpo)> GetConCookieAsync(string ruta)
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
            peticion.Headers.Add(CabeceraCookieSimulada, "1");
            return await EnviarAsync(peticion);
        }

        public async Task<string> DetalleAsync(HttpResponseMessage respuesta) =>
            _excepciones.Count > 0 ? string.Join("\n", _excepciones) : await respuesta.Content.ReadAsStringAsync();

        private async Task<(HttpStatusCode Estado, string Cuerpo)> EnviarAsync(HttpRequestMessage peticion)
        {
            var respuesta = await _http.SendAsync(peticion);
            return (respuesta.StatusCode, await DetalleAsync(respuesta));
        }

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// El papel de la cookie de Identity en <c>Program.cs</c>: esquema por defecto que autentica sin clave. Es un
    /// usuario administrador del Tenant, justo lo que no puede colarse en <c>/api/v1</c>.
    /// </summary>
    private sealed class CookieSimuladaHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Esquema = "CookieSimulada";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(CabeceraCookieSimulada))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identidad = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, Roles.Administrador)], Esquema);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidad), Esquema)));
        }
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinCircuitoDeBlazor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
