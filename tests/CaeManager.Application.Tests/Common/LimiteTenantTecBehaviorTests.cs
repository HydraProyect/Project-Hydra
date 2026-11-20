using System.Runtime.CompilerServices;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// D3 (decisiones de Chris del 2026-11-19): <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/> cierra los Commands
/// de alta de Centros, de Clientes empresariales, de delegaciones y las importaciones cuando Outbound está cerrado para el
/// Tenant beneficiario o, sobre una delegación, para el Tenant de origen de su Operador CAE (EP-4), y deja pasar todo lo
/// demás. La tarifa del Operador CAE, la cartera, la edición de una Empresa (<c>EditarEmpresaCommand</c>) y la Papelera de
/// Empresas se cierran en su handler (<c>cerrado-handler</c> en <c>LimiteTenantTecClasificacion.txt</c>). La lista de tipos de este test se escribe
/// a mano, aparte de <see cref="RegistroLimiteTenantTec"/>: si alguien quita una entrada del registro, este test da rojo
/// (la mutación «quitar la guarda de un Command»). Qué es «cerrado» (marcador y línea) lo prueba
/// <see cref="OutboundCerradoTenantTecServiceTests"/>; la composición con PostgreSQL, <c>LimiteTenantTecBajoRuntimeTests</c>.
/// </summary>
public class LimiteTenantTecBehaviorTests
{
    private const string Raiz = "CaeManager.Application.";

    /// <summary>Los Commands que D3 cierra, por nombre completo relativo a <c>CaeManager.Application</c>.</summary>
    public static readonly TheoryData<string> Cerrados =
    [
        "Centros.Commands.CrearCentro.CrearCentroCommand",
        "Centros.Commands.RestaurarCentro.RestaurarCentroCommand",
        "Centros.Commands.CrearCanalGestion.CrearCanalGestionCommand",
        "Centros.Commands.EditarCanalGestion.EditarCanalGestionCommand",
        "Centros.Commands.MarcarCanalGestionPrincipal.MarcarCanalGestionPrincipalCommand",
        "Clientes.Commands.CrearCliente.CrearClienteCommand",
        "Clientes.Commands.RestaurarCliente.RestaurarClienteCommand",
        "Clientes.Commands.EditarCliente.EditarClienteCommand",
        "Clientes.Commands.ReasignarEjecutivoCliente.ReasignarEjecutivoClienteCommand",
        "Clientes.Commands.GuardarPlataformaCaeCliente.GuardarPlataformaCaeClienteCommand",
        "Clientes.Commands.GuardarAccesoPlataformaCae.GuardarAccesoPlataformaCaeCommand",
        "Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommand",
        "Tenants.Commands.ReactivarDelegacionTenant.ReactivarDelegacionTenantCommand",
        "Tenants.Commands.CrearAsignacionOperadorDelegado.CrearAsignacionOperadorDelegadoCommand",
        "Tenants.Commands.RegistrarEncargoAdministracion.RegistrarEncargoAdministracionCommand",
        "Importacion.Commands.EjecutarImportacion.EjecutarImportacionCommand",
        "Importacion.Commands.EjecutarImportacionCombinada.EjecutarImportacionCombinadaCommand",
        "Importacion.Commands.EjecutarImportacionMaestra.EjecutarImportacionMaestraCommand",
        "Importacion.Commands.EjecutarImportacionMaestra.PrevisualizarImportacionMaestraCommand",
        "Importacion.Commands.RegistrarHistorialImportacion.RegistrarHistorialImportacionCommand",
    ];

    /// <summary>
    /// Los que D3 cierra en su handler, porque el Tenant beneficiario sale de los datos (la Asignación de Cartera, el Operador
    /// CAE de la tarifa o las Relaciones Empresariales de la Empresa): el behavior los deja pasar sin leer la base, y su
    /// guarda se prueba en el test de cada handler.
    /// </summary>
    public static readonly TheoryData<string> CerradosEnSuHandler =
    [
        "Operaciones.ApoyoCartera.Commands.ProponerApoyoCarteraCommand",
        "Operaciones.ApoyoCartera.Commands.AceptarPropuestaApoyoCarteraCommand",
        "Operaciones.IncorporacionCartera.Commands.SolicitarIncorporacionCarteraCommand",
        "Operaciones.IncorporacionCartera.Commands.AceptarSolicitudIncorporacionCarteraCommand",
        "Usuarios.Commands.AsignarCarteraGestorCae.AsignarCarteraGestorCaeCommand",
        "Usuarios.Commands.CrearUsuario.CrearUsuarioCommand",
        "Usuarios.Commands.CambiarActivacionUsuario.CambiarActivacionUsuarioCommand",
        "Dashboard.TarifasOperadorCae.FijarTarifaOperadorCaeCommand",
        "Empresas.Commands.EditarEmpresa.EditarEmpresaCommand",
        "Empresas.Commands.RestaurarEmpresa.RestaurarEmpresaCommand",
    ];

    /// <summary>
    /// Lo que D3 deja abierto aunque Outbound esté cerrado: las bajas (DN-7), la edición de la identidad de un Centro, las
    /// Subcontratas (abiertas por D5), las Comunicaciones (DN-5), que esperan su decisión, y el propio comando de plataforma.
    /// </summary>
    public static readonly TheoryData<string> Abiertos =
    [
        "Centros.Commands.EliminarCentro.EliminarCentroCommand",
        "Centros.Commands.EliminarCentros.EliminarCentrosCommand",
        "Centros.Commands.EliminarCanalGestion.EliminarCanalGestionCommand",
        "Centros.Commands.EditarCentro.EditarCentroCommand",
        "Clientes.Commands.EliminarCliente.EliminarClienteCommand",
        "Clientes.Commands.EliminarClientes.EliminarClientesCommand",
        "Tenants.Commands.DesactivarDelegacionTenant.DesactivarDelegacionTenantCommand",
        "Tenants.Commands.RevocarAsignacionOperadorDelegado.RevocarAsignacionOperadorDelegadoCommand",
        "Tenants.Commands.RetirarEncargoAdministracion.RetirarEncargoAdministracionCommand",
        "Operaciones.ApoyoCartera.Commands.RevocarApoyoCarteraCommand",
        "Operaciones.IncorporacionCartera.Commands.RevocarIncorporacionCarteraCommand",
        "Subcontratas.Commands.CrearSubcontrata.CrearSubcontrataCommand",
        "Tenants.Commands.MarcarParticipanteTec.MarcarTenantComoParticipanteTecCommand",
        "Tenants.Commands.GuardarLogoTenant.GuardarLogoTenantCommand",
    ];

    private static readonly Guid TenantActivo = Guid.NewGuid();

    private static Type Tipo(string relativo) =>
        typeof(ICommandBase).Assembly.GetType(Raiz + relativo)
        ?? throw new InvalidOperationException($"{relativo} no existe: el test nombra un Command que ya no está.");

    /// <summary>Una instancia del Command sin pasar por su constructor: al behavior solo le importa el tipo.</summary>
    private static object Instancia(string relativo) => RuntimeHelpers.GetUninitializedObject(Tipo(relativo));

    private static async Task<(TResponse Respuesta, bool LlegoAlHandler)> Ejecutar<TRequest, TResponse>(
        TRequest request, TResponse exito, Guid? tenantActivo, IOutboundCerradoTenantTec limite,
        IDelegacionTenantRepository? delegaciones = null) where TRequest : notnull
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(limite);
        if (delegaciones is not null)
            servicios.AddSingleton(delegaciones);
        await using var proveedor = servicios.BuildServiceProvider();

        var llego = false;
        var respuesta = await new LimiteTenantTecBehavior<TRequest, TResponse>(new TenantActualFijo(tenantActivo), proveedor)
            .Handle(request, _ => { llego = true; return Task.FromResult(exito); }, default);
        return (respuesta, llego);
    }

    private static void DebeSerElErrorDeD3(Result resultado)
    {
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        resultado.Error.Mensaje.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Mensaje, "el mismo error en todos los puntos");
    }

    [Theory]
    [MemberData(nameof(Cerrados))]
    public async Task Con_Outbound_cerrado_el_Command_no_llega_al_handler(string comando)
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var (respuesta, llego) = await Ejecutar<object, Result>(Instancia(comando), Result.Exito(), TenantActivo, limite);

        llego.Should().BeFalse();
        DebeSerElErrorDeD3(respuesta);
        limite.Preguntados.Should().StartWith(TenantActivo, "decide por el Tenant beneficiario, que es el Tenant activo");
    }

    [Theory]
    [MemberData(nameof(Cerrados))]
    public async Task Con_Outbound_abierto_el_Command_llega_al_handler(string comando)
    {
        // Una delegación del propio Tenant activo para cualquier Id: sin ella, un IComandoSobreDelegacionTenant se deniega.
        var (respuesta, llego) = await Ejecutar<object, Result>(
            Instancia(comando), Result.Exito(), TenantActivo, OutboundCerradoTenantTecFijo.Abierto(),
            new DelegacionesFalsas { ParaCualquierId = new DelegacionTenant(Guid.NewGuid(), TenantActivo) });

        llego.Should().BeTrue();
        respuesta.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Cerrados))]
    public async Task Sin_Tenant_activo_el_Command_cerrado_se_deniega(string comando)
    {
        var limite = OutboundCerradoTenantTecFijo.Abierto();
        var (respuesta, llego) = await Ejecutar<object, Result>(Instancia(comando), Result.Exito(), null, limite);

        llego.Should().BeFalse("sin Tenant beneficiario no hay con qué decidir, y la guarda falla cerrada");
        DebeSerElErrorDeD3(respuesta);
        limite.Preguntados.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Abiertos))]
    public async Task Lo_que_D3_deja_abierto_pasa_sin_leer_la_base(string comando)
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var (respuesta, llego) = await Ejecutar<object, Result>(Instancia(comando), Result.Exito(), TenantActivo, limite);

        llego.Should().BeTrue();
        respuesta.EsExitoso.Should().BeTrue();
        limite.Preguntados.Should().BeEmpty("un Command fuera del registro no consulta el marcador");
    }

    [Theory]
    [MemberData(nameof(CerradosEnSuHandler))]
    public async Task Lo_que_D3_cierra_en_su_handler_pasa_el_behavior_sin_leer_la_base(string comando)
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var (_, llego) = await Ejecutar<object, Result>(Instancia(comando), Result.Exito(), TenantActivo, limite);

        llego.Should().BeTrue("su Tenant beneficiario no es el Tenant activo, o depende de los datos: decide el handler");
        limite.Preguntados.Should().BeEmpty();
        RegistroLimiteTenantTec.CierraSinLinea(Tipo(comando)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Cerrados))]
    public void El_registro_cierra_cada_Command_de_la_lista(string comando)
    {
        RegistroLimiteTenantTec.CierraSinLinea(Tipo(comando)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Abiertos))]
    public void El_registro_no_cierra_lo_que_queda_abierto(string comando)
    {
        RegistroLimiteTenantTec.CierraSinLinea(Tipo(comando)).Should().BeFalse();
    }

    [Fact]
    public void Cada_clave_del_registro_nombra_al_menos_un_Command()
    {
        var comandos = typeof(ICommandBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICommandBase).IsAssignableFrom(t))
            .Select(t => t.FullName![Raiz.Length..])
            .ToList();

        foreach (var clave in RegistroLimiteTenantTec.CerradasSinLinea)
            comandos.Should().Contain(c => RegistroLimiteTenantTec.Casa(c, clave),
                $"la clave «{clave}» tiene que seguir nombrando un Command: un renombrado no puede abrir en silencio");
        foreach (var clave in RegistroLimiteTenantTec.PendientesDeDecision.Keys.Concat(RegistroLimiteTenantTec.AbiertasAlTenantTec.Keys))
            comandos.Should().Contain(c => RegistroLimiteTenantTec.Casa(c, clave));
    }

    [Fact]
    public void Ninguna_clave_cerrada_cae_en_un_area_pendiente_de_decision()
    {
        RegistroLimiteTenantTec.CerradasSinLinea
            .Where(c => RegistroLimiteTenantTec.PendientesDeDecision.Keys.Any(p => RegistroLimiteTenantTec.Casa(c, p)))
            .Should().BeEmpty("Comunicaciones (DN-5) no se cierra en D3");
    }

    [Fact]
    public void Ninguna_clave_cerrada_cae_en_un_area_abierta_al_Tenant_TEC()
    {
        RegistroLimiteTenantTec.CerradasSinLinea
            .Where(c => RegistroLimiteTenantTec.AbiertasAlTenantTec.Keys.Any(a =>
                RegistroLimiteTenantTec.Casa(c, a) || RegistroLimiteTenantTec.Casa(a, c)))
            .Should().BeEmpty("D5 abre las Subcontratas al Tenant TEC: ni una clave cerrada dentro, ni una que las englobe");
        RegistroLimiteTenantTec.AbiertasAlTenantTec.Keys
            .Where(a => RegistroLimiteTenantTec.PendientesDeDecision.Keys.Any(p => RegistroLimiteTenantTec.Casa(a, p) || RegistroLimiteTenantTec.Casa(p, a)))
            .Should().BeEmpty("lo que una decisión ya abrió no sigue pendiente");
    }

    /// <summary>
    /// D5 (DECISION_LOG del 2026-11-19): las Subcontratas se abren al Tenant TEC. Todos los Commands del área salen de lo
    /// pendiente y el registro no cierra ninguno entero; la lista se escribe a mano para que retirar la entrada dé rojo. Lo que
    /// D5 no abre (la Relación Empresarial con Clientes empresariales al crear, editar o restaurar una Subcontrata) se cierra
    /// por datos o en el handler, no aquí (<c>SubcontratasAbiertasAlTenantTecTests</c>, <c>SubcontrataLimiteTenantTecTests</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(CommandsDeSubcontratas))]
    public void Las_Subcontratas_estan_abiertas_al_Tenant_TEC_por_D5(string comando)
    {
        RegistroLimiteTenantTec.EstaAbiertaAlTenantTec(Tipo(comando)).Should().BeTrue();
        RegistroLimiteTenantTec.EstaPendiente(Tipo(comando)).Should().BeFalse("D5 ya está decidida");
        RegistroLimiteTenantTec.CierraSinLinea(Tipo(comando)).Should().BeFalse();
        RegistroLimiteTenantTec.AbiertasAlTenantTec["Subcontratas"].Should().Contain("D5");
    }

    /// <summary>Los doce Commands de <c>Application/Subcontratas</c>, por nombre completo relativo.</summary>
    private static readonly string[] NombresDeCommandsDeSubcontratas =
    [
        "Subcontratas.Commands.ActualizarVerificacionExterna.ActualizarVerificacionExternaSubcontrataCommand",
        "Subcontratas.Commands.CambiarNivelServicioSubcontrata.CambiarNivelServicioSubcontrataCommand",
        "Subcontratas.Commands.CrearSubcontrata.CrearSubcontrataCommand",
        "Subcontratas.Commands.EditarSubcontrata.EditarSubcontrataCommand",
        "Subcontratas.Commands.EliminarSubcontrata.EliminarSubcontrataCommand",
        "Subcontratas.Commands.EliminarSubcontratas.EliminarSubcontratasCommand",
        "Subcontratas.Commands.EliminarVerificacionExterna.EliminarVerificacionExternaSubcontrataCommand",
        "Subcontratas.Commands.GuardarCredencialAccesoSubcontrata.GuardarCredencialAccesoSubcontrataCommand",
        "Subcontratas.Commands.GuardarNotaInternaSubcontrata.GuardarNotaInternaSubcontrataCommand",
        "Subcontratas.Commands.RegistrarVerificacionExterna.RegistrarVerificacionExternaSubcontrataCommand",
        "Subcontratas.Commands.RestaurarSubcontrata.RestaurarSubcontrataCommand",
        "Subcontratas.Commands.RestaurarVerificacionExterna.RestaurarVerificacionExternaSubcontrataCommand",
    ];

    public static readonly TheoryData<string> CommandsDeSubcontratas = new(NombresDeCommandsDeSubcontratas);

    [Fact]
    public void La_lista_de_Commands_de_Subcontratas_es_completa()
    {
        var delArea = typeof(ICommandBase).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICommandBase).IsAssignableFrom(t)
                        && t.FullName!.StartsWith(Raiz + "Subcontratas.", StringComparison.Ordinal))
            .Select(t => t.FullName![Raiz.Length..])
            .ToList();

        delArea.Should().BeEquivalentTo(NombresDeCommandsDeSubcontratas,
            "un Command nuevo de Subcontratas entra en las pruebas de D5 en el mismo commit");
    }

    [Fact]
    public async Task El_fallo_de_un_Command_con_valor_es_un_Result_de_su_tipo()
    {
        var (respuesta, llego) = await Ejecutar<object, Result<Guid>>(
            Instancia("Centros.Commands.CrearCentro.CrearCentroCommand"), Result.Exito(Guid.NewGuid()), TenantActivo,
            OutboundCerradoTenantTecFijo.Cerrado());

        llego.Should().BeFalse();
        respuesta.EsExitoso.Should().BeFalse();
        respuesta.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
    }

    [Fact]
    public async Task Una_consulta_no_se_mira()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var (_, llego) = await Ejecutar<Consulta, Result>(new Consulta(), Result.Exito(), TenantActivo, limite);

        llego.Should().BeTrue();
        limite.Preguntados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_excepcion_al_leer_se_propaga()
    {
        var limite = new OutboundCerradoTenantTecFijo(_ => throw new InvalidOperationException("lectura caída"));

        var accion = () => Ejecutar<object, Result>(
            Instancia("Centros.Commands.CrearCentro.CrearCentroCommand"), Result.Exito(), TenantActivo, limite);

        await accion.Should().ThrowAsync<InvalidOperationException>("una lectura caída no abre la guarda");
    }

    [Fact]
    public async Task Crear_una_Empresa_relacionada_con_Clientes_empresariales_se_cierra()
    {
        var comando = new CrearEmpresaCommand("Empresa de prueba", "B12345674", [Guid.NewGuid()]);
        var (respuesta, llego) = await Ejecutar<CrearEmpresaCommand, Result>(
            comando, Result.Exito(), TenantActivo, OutboundCerradoTenantTecFijo.Cerrado());

        llego.Should().BeFalse();
        DebeSerElErrorDeD3(respuesta);
    }

    [Fact]
    public async Task Crear_la_Empresa_propia_sin_Clientes_empresariales_sigue_abierto()
    {
        var limite = OutboundCerradoTenantTecFijo.Cerrado();
        var (respuesta, llego) = await Ejecutar<CrearEmpresaCommand, Result>(
            new CrearEmpresaCommand("Empresa propia de prueba", "B12345674", []), Result.Exito(), TenantActivo, limite);

        llego.Should().BeTrue("DN-4: la Empresa propia sin Clientes empresariales sigue abierta");
        respuesta.EsExitoso.Should().BeTrue();
        limite.Preguntados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sobre_una_delegacion_decide_tambien_por_su_Tenant_beneficiario()
    {
        // Vía heredada: el Tenant activo es el del Operador CAE externo (abierto) y la delegación es de otro Tenant
        // beneficiario, marcado y sin línea Outbound.
        var beneficiario = Guid.NewGuid();
        var delegacion = new DelegacionTenant(TenantActivo, beneficiario);
        var limite = new OutboundCerradoTenantTecFijo(t => t == beneficiario);

        var (respuesta, llego) = await Ejecutar<CrearAsignacionOperadorDelegadoCommand, Result>(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"), Result.Exito(), TenantActivo,
            limite, new DelegacionesFalsas(delegacion));

        llego.Should().BeFalse();
        DebeSerElErrorDeD3(respuesta);
        limite.Preguntados.Should().Equal(TenantActivo, beneficiario);
    }

    [Fact]
    public async Task Reactivar_una_delegacion_de_un_Tenant_beneficiario_cerrado_se_deniega()
    {
        var beneficiario = Guid.NewGuid();
        var delegacion = new DelegacionTenant(TenantActivo, beneficiario);

        var (respuesta, llego) = await Ejecutar<ReactivarDelegacionTenantCommand, Result>(
            new ReactivarDelegacionTenantCommand(delegacion.Id), Result.Exito(), TenantActivo,
            new OutboundCerradoTenantTecFijo(t => t == beneficiario), new DelegacionesFalsas(delegacion));

        llego.Should().BeFalse("DN-7: las reactivaciones se deniegan");
        DebeSerElErrorDeD3(respuesta);
    }

    [Fact]
    public async Task Sobre_una_delegacion_decide_tambien_por_el_Tenant_de_origen_de_su_Operador_CAE()
    {
        // EP-4 («ni Operador CAE», ronda 3 del revisor-puente): el Tenant activo es el Tenant beneficiario (abierto) y el
        // Operador CAE de la delegación es un Tenant TEC marcado y sin línea Outbound. Ni se reactiva ni recibe operador.
        var operadorCae = Guid.NewGuid();
        var delegacion = new DelegacionTenant(operadorCae, TenantActivo);

        foreach (var comando in new ICommandBase[]
                 {
                     new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
                     new ReactivarDelegacionTenantCommand(delegacion.Id),
                 })
        {
            var limite = new OutboundCerradoTenantTecFijo(t => t == operadorCae);
            var (respuesta, llego) = await Ejecutar<ICommandBase, Result>(
                comando, Result.Exito(), TenantActivo, limite, new DelegacionesFalsas(delegacion));

            llego.Should().BeFalse(comando.GetType().Name);
            DebeSerElErrorDeD3(respuesta);
            limite.Preguntados.Should().Equal(TenantActivo, operadorCae);
        }
    }

    [Fact]
    public async Task Sobre_una_delegacion_con_su_Operador_CAE_con_linea_Outbound_vigente_pasa()
    {
        // El mismo Operador CAE, marcado pero con línea P Vigente: la guarda responde abierto y la petición pasa.
        var operadorCae = Guid.NewGuid();
        var delegacion = new DelegacionTenant(operadorCae, TenantActivo);
        var limite = OutboundCerradoTenantTecFijo.Abierto();

        var (_, llego) = await Ejecutar<ReactivarDelegacionTenantCommand, Result>(
            new ReactivarDelegacionTenantCommand(delegacion.Id), Result.Exito(), TenantActivo, limite,
            new DelegacionesFalsas(delegacion));

        llego.Should().BeTrue();
        limite.Preguntados.Should().Equal(TenantActivo, operadorCae);
    }

    [Fact]
    public async Task Sobre_una_delegacion_con_su_Tenant_beneficiario_abierto_pasa()
    {
        var delegacion = new DelegacionTenant(TenantActivo, Guid.NewGuid());

        var (_, llego) = await Ejecutar<CrearAsignacionOperadorDelegadoCommand, Result>(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"), Result.Exito(), TenantActivo,
            OutboundCerradoTenantTecFijo.Abierto(), new DelegacionesFalsas(delegacion));

        llego.Should().BeTrue();
    }

    [Fact]
    public async Task Si_la_delegacion_no_aparece_se_deniega()
    {
        // Falla cerrado: sin la DelegacionTenant (no existe, o el Tenant activo no la ve) no se sabe cuál es su Tenant
        // beneficiario, aunque el Tenant activo tenga Outbound abierto.
        var limite = OutboundCerradoTenantTecFijo.Abierto();

        foreach (var comando in new ICommandBase[]
                 {
                     new CrearAsignacionOperadorDelegadoCommand(Guid.NewGuid(), Guid.NewGuid(), "GestorCae"),
                     new ReactivarDelegacionTenantCommand(Guid.NewGuid()),
                 })
        {
            var (respuesta, llego) = await Ejecutar<object, Result>(
                comando, Result.Exito(), TenantActivo, limite, new DelegacionesFalsas());

            llego.Should().BeFalse(comando.GetType().Name);
            DebeSerElErrorDeD3(respuesta);
        }
    }

    [Fact]
    public void No_depende_de_quien_es_el_actor()
    {
        // DN-6: el límite vale igual con Sesión Privilegiada, por un Operador CAE externo o por un Encargo de
        // administración. Ni el behavior ni el servicio pueden mirar al actor si no lo reciben.
        var prohibidos = new[] { typeof(ICurrentUserService), typeof(ISesionPrivilegiadaActual) };
        foreach (var tipo in new[] { typeof(LimiteTenantTecBehavior<object, Result>), typeof(OutboundCerradoTenantTecService) })
            tipo.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Should().NotContain(prohibidos, $"{tipo.Name} decide por el Tenant beneficiario, no por el actor");
    }

    private sealed record Consulta : MediatR.IRequest<Result>;

    private sealed class DelegacionesFalsas(params DelegacionTenant[] delegaciones) : IDelegacionTenantRepository
    {
        /// <summary>La que se devuelve para un Id que no está en la lista; sin ella, la delegación no aparece.</summary>
        public DelegacionTenant? ParaCualquierId { get; init; }

        public Task<DelegacionTenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(delegaciones.FirstOrDefault(d => d.Id == id) ?? ParaCualquierId);

        public Task<bool> BloquearYReleerAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExisteActivaAsync(Guid tenantConsultoraId, Guid tenantClienteId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Agregar(DelegacionTenant delegacion) => throw new NotSupportedException();
    }
}
