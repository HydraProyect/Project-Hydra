using CaeManager.Application.Clientes.Commands.GuardarAccesoPlataformaCae;
using CaeManager.Application.Clientes.Commands.GuardarPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerAccesosPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerPlataformaCaeCliente;
using CaeManager.Application.Clientes.Queries.RevelarContrasenaAccesoPlataformaCae;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Auditoria;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

/// <summary>
/// Caja «Plataforma CAE» de Cliente 360 (decisión del 2026-10-18): la plataforma es del Cliente
/// empresarial y el acceso (usuario y contraseña) de cada par (Cliente empresarial, Empresa). Los
/// handlers exigen la cartera de gestión sobre las dos partes y el lado de gestión (el Usuario de
/// Cliente no la ve); los comportamientos del pipeline dejan fuera a Consulta, al rol Cliente y a
/// toda Sesión Privilegiada, impersonación incluida. La contraseña solo sale por «Revelar», que
/// la registra en la auditoría antes de devolverla.
/// </summary>
public class PlataformaCaeClienteTests
{
    private static readonly DateTime Ahora = new(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Escenario
    {
        public Empresa Cliente { get; } = Empresa.CrearComoCliente("Cliente Industrial S.A.", "B12345674", false, null, null);
        public Empresa Empresa { get; } = new("Empresa Propia S.L.");
        public Empresa OtraEmpresa { get; } = new("Otra Propia S.L.");
        public EmpresasQueryContextFalso Contexto { get; } = new();
        public EmpresaRepositorioFalso Empresas { get; } = new();
        public PlataformaRepositorioFalso Plataformas { get; }
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public RegistroAccesoDatoSensibleFalso Registro { get; } = new();
        public Guid UsuarioId { get; } = Guid.NewGuid();

        public Escenario()
        {
            Plataformas = new PlataformaRepositorioFalso(Contexto);
            foreach (var e in new[] { Cliente, Empresa, OtraEmpresa })
            {
                Contexto.ListaEmpresas.Add(e);
                Empresas.Agregar(e);
            }

            Contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(Empresa.Id, Cliente.Id, Ahora.AddYears(-1)));
            Contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(OtraEmpresa.Id, Cliente.Id, Ahora.AddYears(-1)));
        }

        /// <summary>Gestor CAE con el Cliente empresarial y las dos Empresas en su Asignación de Cartera.</summary>
        public AlcanceDatosServiceFalso EnCartera(bool ladoDeGestion = true, IReadOnlyList<Guid>? empresasParaGestion = null) =>
            new(tieneAccesoTotal: false, clienteIdsVisibles: [Cliente.Id], empresaIdsVisibles: [Empresa.Id, OtraEmpresa.Id],
                empresaIdsParaGestion: empresasParaGestion, ladoDeGestion: ladoDeGestion);

        public static AlcanceDatosServiceFalso FueraDeCartera() =>
            new(tieneAccesoTotal: false, clienteIdsVisibles: [], empresaIdsVisibles: []);

        public PlataformaCaeClienteEmpresarial RegistrarPlataforma()
        {
            var plataforma = new PlataformaCaeClienteEmpresarial(Cliente.Id, "Nalanda", "https://portal.example", "CLI-01");
            Contexto.ListaPlataformasCae.Add(plataforma);
            return plataforma;
        }

        public AccesoPlataformaCaeEmpresa RegistrarAcceso(Empresa empresa, string? usuario, string? contrasena)
        {
            var acceso = new AccesoPlataformaCaeEmpresa(Cliente.Id, empresa.Id);
            acceso.CambiarUsuario(usuario);
            if (contrasena is not null)
                acceso.CambiarContrasena(contrasena, UsuarioId, Ahora);
            Contexto.ListaAccesosPlataformaCae.Add(acceso);
            return acceso;
        }

        public GuardarPlataformaCaeClienteCommandHandler GuardarPlataforma(IAlcanceDatosService alcance) =>
            new(Empresas, Plataformas, alcance, UnitOfWork);

        public GuardarAccesoPlataformaCaeCommandHandler GuardarAcceso(IAlcanceDatosService alcance) =>
            new(Contexto, Plataformas, alcance, new UsuarioActualFalso("GestorCae", UsuarioId), UnitOfWork);

        public ObtenerPlataformaCaeClienteQueryHandler ObtenerPlataforma(
            IAlcanceDatosService alcance, string? rol = "GestorCae", CapacidadPrivilegio? capacidad = null) =>
            new(Contexto, alcance, new UsuarioActualFalso(rol, Guid.NewGuid()), Sesion(capacidad));

        public ObtenerAccesosPlataformaCaeClienteQueryHandler ObtenerAccesos(IAlcanceDatosService alcance) =>
            new(Contexto, alcance, new DirectorioFalso(new Dictionary<Guid, string> { [UsuarioId] = "Marta Gestora" }), Registro);

        public RevelarContrasenaAccesoPlataformaCaeQueryHandler Revelar(IAlcanceDatosService alcance, RegistroAccesoDatoSensibleFalso? registro = null) =>
            new(Contexto, alcance, registro ?? Registro);
    }

    // ── Guardar la plataforma del Cliente empresarial ──

    [Fact]
    public async Task Guardar_plataforma_la_crea_y_una_segunda_vez_la_actualiza_sin_duplicarla()
    {
        var e = new Escenario();

        (await e.GuardarPlataforma(e.EnCartera()).Handle(
            new GuardarPlataformaCaeClienteCommand(e.Cliente.Id, "Nalanda", "https://a.example", "A"), default)).EsExitoso.Should().BeTrue();
        (await e.GuardarPlataforma(e.EnCartera()).Handle(
            new GuardarPlataformaCaeClienteCommand(e.Cliente.Id, "Dokify", null, null), default)).EsExitoso.Should().BeTrue();

        var plataforma = e.Contexto.ListaPlataformasCae.Should().ContainSingle().Subject;
        plataforma.ClienteEmpresarialId.Should().Be(e.Cliente.Id);
        plataforma.Nombre.Should().Be("Dokify");
        plataforma.UrlPortal.Should().BeNull();
        e.UnitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Guardar_plataforma_fuera_de_la_cartera_responde_como_si_no_existiera()
    {
        var e = new Escenario();

        var resultado = await e.GuardarPlataforma(Escenario.FueraDeCartera()).Handle(
            new GuardarPlataformaCaeClienteCommand(e.Cliente.Id, "Nalanda", null, null), default);

        resultado.EsFallido.Should().BeTrue();
        e.Contexto.ListaPlataformasCae.Should().BeEmpty();
        e.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Guardar_plataforma_desde_el_rol_Cliente_se_deniega()
    {
        var e = new Escenario();

        var resultado = await e.GuardarPlataforma(e.EnCartera(ladoDeGestion: false)).Handle(
            new GuardarPlataformaCaeClienteCommand(e.Cliente.Id, "Nalanda", null, null), default);

        resultado.EsFallido.Should().BeTrue();
        e.Contexto.ListaPlataformasCae.Should().BeEmpty();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("portal.example")]
    public void El_validador_rechaza_una_direccion_que_no_es_http_ni_https(string url)
    {
        var validacion = new GuardarPlataformaCaeClienteCommandValidator().Validate(
            new GuardarPlataformaCaeClienteCommand(Guid.NewGuid(), "Nalanda", url, null));

        validacion.IsValid.Should().BeFalse();
    }

    // ── Guardar el acceso de una Empresa ──

    [Fact]
    public async Task Guardar_acceso_sin_plataforma_registrada_falla()
    {
        var e = new Escenario();

        var resultado = await e.GuardarAcceso(e.EnCartera()).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "b12345678", "s3cr3ta"), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AccesoPlataformaCae.SinPlataforma");
        e.Contexto.ListaAccesosPlataformaCae.Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_acceso_lo_crea_y_anota_quien_y_cuando_cambio_la_contrasena()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        var resultado = await e.GuardarAcceso(e.EnCartera()).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "b12345678", "s3cr3ta"), default);

        resultado.EsExitoso.Should().BeTrue();
        var acceso = e.Contexto.ListaAccesosPlataformaCae.Should().ContainSingle().Subject;
        acceso.ClienteEmpresarialId.Should().Be(e.Cliente.Id);
        acceso.EmpresaId.Should().Be(e.Empresa.Id);
        acceso.Usuario.Should().Be("b12345678");
        acceso.Contrasena.Should().Be("s3cr3ta");
        acceso.ContrasenaCambiadaPorUsuarioId.Should().Be(e.UsuarioId);
        acceso.ContrasenaCambiadaEnUtc.Should().NotBeNull();
        e.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Guardar_acceso_con_la_contrasena_vacia_conserva_la_guardada(string? contrasenaEnviada)
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        var acceso = e.RegistrarAcceso(e.Empresa, "usuario-viejo", "secreta-guardada");

        var resultado = await e.GuardarAcceso(e.EnCartera()).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "usuario-nuevo", contrasenaEnviada), default);

        resultado.EsExitoso.Should().BeTrue();
        e.Contexto.ListaAccesosPlataformaCae.Should().ContainSingle();
        acceso.Usuario.Should().Be("usuario-nuevo");
        acceso.Contrasena.Should().Be("secreta-guardada", "el formulario no precarga la contraseña: vacío no significa borrarla");
        acceso.ContrasenaCambiadaEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public async Task Guardar_acceso_de_una_Empresa_fuera_de_la_cartera_de_gestion_se_deniega()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        var resultado = await e.GuardarAcceso(e.EnCartera(empresasParaGestion: [e.OtraEmpresa.Id])).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "u", "p"), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AccesoPlataformaCae.NoEncontrado");
        e.Contexto.ListaAccesosPlataformaCae.Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_acceso_de_un_Cliente_empresarial_fuera_de_la_cartera_se_deniega()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        var resultado = await e.GuardarAcceso(Escenario.FueraDeCartera()).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "u", "p"), default);

        resultado.EsFallido.Should().BeTrue();
        e.Contexto.ListaAccesosPlataformaCae.Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_acceso_desde_el_rol_Cliente_se_deniega()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        var resultado = await e.GuardarAcceso(e.EnCartera(ladoDeGestion: false)).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "u", "p"), default);

        resultado.EsFallido.Should().BeTrue();
        e.Contexto.ListaAccesosPlataformaCae.Should().BeEmpty();
    }

    [Fact]
    public async Task Guardar_acceso_de_una_Empresa_que_ya_no_presta_servicio_al_Cliente_se_deniega()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.Contexto.ListaRelacionesEmpresariales.Single(r => r.ProveedoraId == e.Empresa.Id).Cerrar(Ahora);

        var resultado = await e.GuardarAcceso(e.EnCartera()).Handle(
            new GuardarAccesoPlataformaCaeCommand(e.Cliente.Id, e.Empresa.Id, "u", "p"), default);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AccesoPlataformaCae.NoEncontrado");
    }

    // ── Leer la plataforma ──

    [Fact]
    public async Task Obtener_plataforma_sin_registrar_devuelve_la_caja_vacia()
    {
        var e = new Escenario();

        var dto = await e.ObtenerPlataforma(e.EnCartera()).Handle(new ObtenerPlataformaCaeClienteQuery(e.Cliente.Id), default);

        dto.Should().Be(new PlataformaCaeClienteDto(false, null, null, null));
    }

    [Fact]
    public async Task Obtener_plataforma_registrada_devuelve_sus_datos_y_ninguna_credencial()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");

        var dto = await e.ObtenerPlataforma(e.EnCartera()).Handle(new ObtenerPlataformaCaeClienteQuery(e.Cliente.Id), default);

        dto.Should().Be(new PlataformaCaeClienteDto(true, "Nalanda", "https://portal.example", "CLI-01"));
        e.Registro.Registrados.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Consulta", null)]
    [InlineData(null, null)]
    [InlineData("GestorCae", CapacidadPrivilegio.SoporteLectura)]
    [InlineData("GestorCae", CapacidadPrivilegio.Impersonacion)]
    public async Task El_codigo_de_cliente_solo_lo_reciben_los_roles_con_escritura_fuera_de_sesion_privilegiada(
        string? rol, CapacidadPrivilegio? capacidad)
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        var dto = await e.ObtenerPlataforma(e.EnCartera(), rol, capacidad).Handle(new ObtenerPlataformaCaeClienteQuery(e.Cliente.Id), default);

        dto.Should().Be(new PlataformaCaeClienteDto(true, "Nalanda", "https://portal.example", null),
            "la pantalla lo oculta con SoloConEscritura, pero el dato tampoco debe viajar en la respuesta");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Los_roles_con_escritura_reciben_el_codigo_de_cliente(string rol)
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        var dto = await e.ObtenerPlataforma(e.EnCartera(), rol).Handle(new ObtenerPlataformaCaeClienteQuery(e.Cliente.Id), default);

        dto!.CodigoCliente.Should().Be("CLI-01");
    }

    [Fact]
    public async Task Obtener_plataforma_fuera_de_la_cartera_o_desde_el_rol_Cliente_no_pinta_la_caja()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();

        (await e.ObtenerPlataforma(Escenario.FueraDeCartera()).Handle(new ObtenerPlataformaCaeClienteQuery(e.Cliente.Id), default))
            .Should().BeNull();
        (await e.ObtenerPlataforma(e.EnCartera(ladoDeGestion: false)).Handle(new ObtenerPlataformaCaeClienteQuery(e.Cliente.Id), default))
            .Should().BeNull();
    }

    // ── Leer los accesos (sin contraseña) ──

    [Fact]
    public void El_DTO_de_accesos_no_tiene_ningun_campo_de_contrasena_en_claro()
    {
        typeof(AccesoPlataformaCaeDto).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Equals("Contrasena", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Obtener_accesos_sin_plataforma_registrada_devuelve_lista_vacia()
    {
        var e = new Escenario();

        var accesos = await e.ObtenerAccesos(e.EnCartera()).Handle(new ObtenerAccesosPlataformaCaeClienteQuery(e.Cliente.Id), default);

        accesos.Should().BeEmpty();
    }

    [Fact]
    public async Task Obtener_accesos_lista_cada_Empresa_que_presta_servicio_y_audita_el_usuario_entregado()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        var acceso = e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");

        var accesos = await e.ObtenerAccesos(e.EnCartera()).Handle(new ObtenerAccesosPlataformaCaeClienteQuery(e.Cliente.Id), default);

        accesos.Should().BeEquivalentTo(
        [
            new AccesoPlataformaCaeDto(e.Empresa.Id, "Empresa Propia S.L.", "b12345678", true, Ahora, "Marta Gestora"),
            new AccesoPlataformaCaeDto(e.OtraEmpresa.Id, "Otra Propia S.L.", null, false, null, null),
        ]);
        e.Registro.Registrados.Should().Equal((nameof(AccesoPlataformaCaeEmpresa), acceso.Id));
    }

    [Fact]
    public async Task Obtener_accesos_omite_las_Empresas_fuera_de_la_cartera_de_gestion()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");

        var accesos = await e.ObtenerAccesos(e.EnCartera(empresasParaGestion: [e.OtraEmpresa.Id]))
            .Handle(new ObtenerAccesosPlataformaCaeClienteQuery(e.Cliente.Id), default);

        accesos!.Select(a => a.EmpresaId).Should().Equal(e.OtraEmpresa.Id);
        e.Registro.Registrados.Should().BeEmpty("el usuario de la Empresa fuera de cartera no se ha leído");
    }

    [Fact]
    public async Task Obtener_accesos_fuera_de_la_cartera_o_desde_el_rol_Cliente_devuelve_null()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");

        (await e.ObtenerAccesos(Escenario.FueraDeCartera()).Handle(new ObtenerAccesosPlataformaCaeClienteQuery(e.Cliente.Id), default))
            .Should().BeNull();
        (await e.ObtenerAccesos(e.EnCartera(ladoDeGestion: false)).Handle(new ObtenerAccesosPlataformaCaeClienteQuery(e.Cliente.Id), default))
            .Should().BeNull();
        e.Registro.Registrados.Should().BeEmpty();
    }

    // ── Revelar la contraseña ──

    [Fact]
    public async Task Revelar_devuelve_la_contrasena_y_la_deja_en_la_auditoria()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        var acceso = e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");

        var dto = await e.Revelar(e.EnCartera()).Handle(
            new RevelarContrasenaAccesoPlataformaCaeQuery(e.Cliente.Id, e.Empresa.Id), default);

        dto!.Contrasena.Should().Be("s3cr3ta");
        e.Registro.Registrados.Should().Equal((nameof(AccesoPlataformaCaeEmpresa), acceso.Id));
    }

    [Fact]
    public async Task Revelar_sin_registro_de_auditoria_no_entrega_la_contrasena()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");

        var revelar = async () => await e.Revelar(e.EnCartera(), new RegistroAccesoDatoSensibleFalso(falla: true)).Handle(
            new RevelarContrasenaAccesoPlataformaCaeQuery(e.Cliente.Id, e.Empresa.Id), default);

        await revelar.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Revelar_fuera_de_la_cartera_desde_el_rol_Cliente_o_de_otra_Empresa_no_devuelve_nada_ni_audita()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.RegistrarAcceso(e.Empresa, "b12345678", "s3cr3ta");
        var consulta = new RevelarContrasenaAccesoPlataformaCaeQuery(e.Cliente.Id, e.Empresa.Id);

        (await e.Revelar(Escenario.FueraDeCartera()).Handle(consulta, default)).Should().BeNull();
        (await e.Revelar(e.EnCartera(ladoDeGestion: false)).Handle(consulta, default)).Should().BeNull();
        (await e.Revelar(e.EnCartera(empresasParaGestion: [e.OtraEmpresa.Id])).Handle(consulta, default)).Should().BeNull();
        e.Registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Revelar_un_acceso_sin_contrasena_no_audita_nada()
    {
        var e = new Escenario();
        e.RegistrarPlataforma();
        e.RegistrarAcceso(e.Empresa, "b12345678", contrasena: null);

        var dto = await e.Revelar(e.EnCartera()).Handle(
            new RevelarContrasenaAccesoPlataformaCaeQuery(e.Cliente.Id, e.Empresa.Id), default);

        dto.Should().BeNull();
        e.Registro.Registrados.Should().BeEmpty();
    }

    // ── Pipeline: roles, segundo factor y Sesión Privilegiada ──

    [Fact]
    public void Las_peticiones_llevan_las_marcas_que_activan_los_comportamientos_de_secretos()
    {
        typeof(RevelarContrasenaAccesoPlataformaCaeQuery).Should().Implement<IConsultaDeSecretosDeTenant>();
        typeof(ObtenerAccesosPlataformaCaeClienteQuery).Should().Implement<IConsultaDeDatosDeCredencial>();
        typeof(GuardarAccesoPlataformaCaeCommand).Should().Implement<IEscrituraDeDatosDeCredencial>();
        typeof(ObtenerPlataformaCaeClienteQuery).Should().NotImplement<IConsultaDeDatosDeCredencial>(
            "la plataforma no es secreta: Consulta ve el nombre y «Abrir portal»");
    }

    public static TheoryData<string?, CapacidadPrivilegio?> SinAccesoALosSecretos => new()
    {
        { "Consulta", null },
        { "Cliente", null },
        { "Administrador", CapacidadPrivilegio.Impersonacion },
        { "Administrador", CapacidadPrivilegio.SoporteLectura },
    };

    [Theory]
    [MemberData(nameof(SinAccesoALosSecretos))]
    public async Task Revelar_y_listar_accesos_no_llegan_al_handler_sin_rol_de_escritura_o_en_sesion_privilegiada(
        string? rol, CapacidadPrivilegio? capacidad)
    {
        var revelar = new AutorizacionSecretosDeTenantBehavior<RevelarContrasenaAccesoPlataformaCaeQuery, ContrasenaAccesoPlataformaCaeDto?>(
            Sesion(capacidad), new UsuarioActualFalso(rol, Guid.NewGuid()));
        var listar = new AutorizacionSecretosDeTenantBehavior<ObtenerAccesosPlataformaCaeClienteQuery, IReadOnlyList<AccesoPlataformaCaeDto>?>(
            Sesion(capacidad), new UsuarioActualFalso(rol, Guid.NewGuid()));
        var handlerLlamado = false;

        var contrasena = await revelar.Handle(new RevelarContrasenaAccesoPlataformaCaeQuery(Guid.NewGuid(), Guid.NewGuid()), _ =>
        {
            handlerLlamado = true;
            return Task.FromResult<ContrasenaAccesoPlataformaCaeDto?>(new("s3cr3ta"));
        }, default);
        var accesos = await listar.Handle(new ObtenerAccesosPlataformaCaeClienteQuery(Guid.NewGuid()), _ =>
        {
            handlerLlamado = true;
            return Task.FromResult<IReadOnlyList<AccesoPlataformaCaeDto>?>([]);
        }, default);

        contrasena.Should().BeNull();
        accesos.Should().BeNull();
        handlerLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task Guardar_acceso_sin_segundo_factor_no_llega_al_handler()
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<GuardarAccesoPlataformaCaeCommand, Result>(
            Sesion(null), new UsuarioActualFalso("GestorCae", Guid.NewGuid(), dobleFactor: false));
        var handlerLlamado = false;

        var guardar = async () => await behavior.Handle(
            new GuardarAccesoPlataformaCaeCommand(Guid.NewGuid(), Guid.NewGuid(), "u", "p"), _ =>
            {
                handlerLlamado = true;
                return Task.FromResult(Result.Exito());
            }, default);

        await guardar.Should().ThrowAsync<SegundoFactorRequeridoParaCredencialesException>();
        handlerLlamado.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SinAccesoALosSecretos))]
    public async Task Los_dos_guardados_se_deniegan_a_Consulta_al_rol_Cliente_y_en_sesion_privilegiada(
        string? rol, CapacidadPrivilegio? capacidad)
    {
        var tenant = new TenantActualFalso(Guid.NewGuid());
        var usuario = new UsuarioActualFalso(rol, Guid.NewGuid());
        var plataforma = new AutorizacionEscrituraBehavior<GuardarPlataformaCaeClienteCommand, Result>(usuario, Sesion(capacidad), tenant);
        var acceso = new AutorizacionEscrituraBehavior<GuardarAccesoPlataformaCaeCommand, Result>(usuario, Sesion(capacidad), tenant);
        var handlerLlamado = false;
        RequestHandlerDelegate<Result> siguiente = _ =>
        {
            handlerLlamado = true;
            return Task.FromResult(Result.Exito());
        };

        var r1 = await plataforma.Handle(new GuardarPlataformaCaeClienteCommand(Guid.NewGuid(), "Nalanda", null, null), siguiente, default);
        var r2 = await acceso.Handle(new GuardarAccesoPlataformaCaeCommand(Guid.NewGuid(), Guid.NewGuid(), "u", "p"), siguiente, default);

        r1.EsFallido.Should().BeTrue();
        r2.EsFallido.Should().BeTrue();
        handlerLlamado.Should().BeFalse();
    }

    private static ISesionPrivilegiadaActual Sesion(CapacidadPrivilegio? capacidad) =>
        new SesionPrivilegiadaFalsa(capacidad is { } c
            ? new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), c, null)
            : null);

    // ── Dobles ──

    private sealed class PlataformaRepositorioFalso(EmpresasQueryContextFalso contexto) : IPlataformaCaeClienteEmpresarialRepository
    {
        public Task<PlataformaCaeClienteEmpresarial?> ObtenerPlataformaAsync(Guid clienteEmpresarialId, CancellationToken cancellationToken = default) =>
            Task.FromResult(contexto.ListaPlataformasCae.SingleOrDefault(p => p.ClienteEmpresarialId == clienteEmpresarialId));

        public void AgregarPlataforma(PlataformaCaeClienteEmpresarial plataforma) => contexto.ListaPlataformasCae.Add(plataforma);

        public Task<AccesoPlataformaCaeEmpresa?> ObtenerAccesoAsync(Guid clienteEmpresarialId, Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(contexto.ListaAccesosPlataformaCae.SingleOrDefault(a => a.ClienteEmpresarialId == clienteEmpresarialId && a.EmpresaId == empresaId));

        public void AgregarAcceso(AccesoPlataformaCaeEmpresa acceso) => contexto.ListaAccesosPlataformaCae.Add(acceso);
    }

    private sealed class UsuarioActualFalso(string? rol, Guid usuarioId, bool dobleFactor = true) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(dobleFactor);
    }

    private sealed class DirectorioFalso(IReadOnlyDictionary<Guid, string> nombres) : IDirectorioUsuariosService
    {
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<bool> EsVisibleEnTenantActualAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(nombres.Where(n => ids.Contains(n.Key)).ToDictionary());
        public Task<bool> EsCuentaActivaConRolAsync(Guid id, Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenant, string rol, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class SesionPrivilegiadaFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
