using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.AutorizacionesLectura;
using CaeManager.Application.Inbound.AutorizacionesLectura.Commands.SubirDocumentoRequerimientoTec;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Inbound.AutorizacionesLectura;

/// <summary>
/// La subida del Tenant TEC a un requerimiento (demo Inbound, P2): el trío se revalida contra la lectura inversa X-2 y contra
/// los sujetos que asigna <see cref="ObtenerRequerimientosTecQuery"/> (la real, con los mismos datos), el tipo propio sale
/// del catálogo por el nombre exacto (D1) y el documento se crea o se renueva con los comandos de siempre, que aquí solo se
/// anotan. Que X-2 solo dé contratos del Tenant TEC del contexto firmado lo mide la integración (solo en CI).
/// </summary>
public class SubirDocumentoRequerimientoTecCommandTests
{
    private const string Administrador = "Administrador";

    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private readonly Guid _contrato = Guid.NewGuid();
    private readonly AutorizacionLecturaRepositorioFalso _autorizaciones = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly List<RequisitoTecDto> _requisitos = [];

    private readonly Empresa _propia = new("Montajes Moncayo S.L.");
    private readonly Trabajador _asignado;
    private readonly Trabajador _noAsignado;
    private readonly TipoDocumento _formacion = new("Formación en altura", null, false, 1, AmbitoAplicacion.Trabajador,
        sensibilidad: SensibilidadDocumental.DatosPersonales);
    private readonly TipoDocumento _seguro = new("Seguro de responsabilidad civil", null, false, 2, AmbitoAplicacion.Empresa,
        sensibilidad: SensibilidadDocumental.SinDatosPersonales);

    public SubirDocumentoRequerimientoTecCommandTests()
    {
        _asignado = Trabajador.DeEmpresa(_propia.Id, "Luis", "Gómez Ara", "12345678Z");
        _noAsignado = Trabajador.DeEmpresa(_propia.Id, "Ana", "Pérez", "00000000T");
        _empresas.ListaEmpresas.Add(_propia);
        _trabajadores.ListaTrabajadores.AddRange([_asignado, _noAsignado]);
        _tipos.ListaTiposDocumento.AddRange([_formacion, _seguro]);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
    }

    private sealed class LectorFalso(IReadOnlyList<RequisitoTecDto> filas) : ILectorRequerimientosTec
    {
        public int Llamadas { get; private set; }

        public Task<IReadOnlyList<RequisitoTecDto>> RequisitosAsync(CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(filas);
        }
    }

    private sealed class PuertaQueNiega : IAutorizacionInbound
    {
        public Task<bool> TieneAsync(CapacidadInbound capacidad, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    /// <summary>Resuelve la Query con su handler real y anota los comandos de Documentos sin ejecutarlos.</summary>
    private sealed class MediadorDePrueba(ObtenerRequerimientosTecQueryHandler consulta) : ISender
    {
        public Guid IdDevuelto { get; } = Guid.NewGuid();

        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerRequerimientosTecQuery query)
                return (TResponse)(object)await consulta.Handle(query, cancellationToken);

            Enviados.Add(request);
            return request is CrearDocumentoCommand or RenovarDocumentoCommand
                ? (TResponse)(object)Result.Exito(IdDevuelto)
                : throw new NotSupportedException($"Petición no prevista: {request.GetType().Name}.");
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private RequisitoTecDto Requisito(string tipo, string entidad = "Trabajador", Guid? contrato = null, string ambito = "GeneralTec")
    {
        var requisito = new RequisitoTecDto(contrato ?? _contrato, "Logística Delta Sur", Guid.NewGuid(), "Planta Zaragoza Norte",
            "Logística Delta Sur S.A.", Guid.NewGuid(), Guid.NewGuid(), tipo, entidad, "Certificado vigente", "Fija", 12, 0, true, ambito,
            "DeCentro", "Todos", 7);
        _requisitos.Add(requisito);
        return requisito;
    }

    private void Autorizar(bool incluyeEmpresa = false, params Trabajador[] trabajadores)
    {
        var autorizacion = AutorizacionLecturaDocumental.Conceder(
            Guid.NewGuid(), _contrato, incluyeEmpresa, _hoy.AddDays(-30), null, Guid.NewGuid(), DateTime.UtcNow);
        _autorizaciones.Agregar(autorizacion);
        foreach (var t in trabajadores)
            _autorizaciones.AgregarTrabajador(autorizacion.AsignarTrabajador(t.Id));
    }

    /// <summary>El Administrador con un papel activo dado (por ejemplo, el del Operador CAE externo que opera en Outbound).</summary>
    private sealed class UsuarioConPapel(PapelActivo papel) : ICurrentUserService
    {
        private readonly Guid _id = Guid.NewGuid();

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(_id);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Administrador);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(Administrador);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
    }

    private (SubirDocumentoRequerimientoTecCommandHandler Handler, MediadorDePrueba Mediador, LectorFalso Lector) Montar(
        IAutorizacionInbound? puerta = null, string? rol = Administrador, ICurrentUserService? usuarioActual = null)
    {
        var lector = new LectorFalso(_requisitos);
        var usuario = usuarioActual ?? new CurrentUserServiceFalso(Guid.NewGuid(), rol);
        var consulta = new ObtenerRequerimientosTecQueryHandler(
            lector, new AutorizacionesLecturaQueryContextFalso(_autorizaciones), _trabajadores, _empresas, _tipos, _documentos,
            _configuracion, usuario, new AutorizacionInboundQuePermite());
        var mediador = new MediadorDePrueba(consulta);
        var handler = new SubirDocumentoRequerimientoTecCommandHandler(
            puerta ?? new AutorizacionInboundQuePermite(), usuario, lector, _tipos, _documentos, mediador);
        return (handler, mediador, lector);
    }

    private SubirDocumentoRequerimientoTecCommand Subida(
        RequisitoTecDto requisito, Guid sujetoId, SujetoRequerimientoTec sujeto = SujetoRequerimientoTec.Trabajador) =>
        new(requisito.ContratoAccesoId, requisito.RequisitoId, sujeto, sujetoId, _hoy.AddDays(-1), _hoy.AddMonths(12),
            "clave-del-pdf", "Curso de 2026");

    [Fact]
    public async Task Un_trio_valido_sin_documento_vigente_crea_el_documento_con_el_tipo_propio_del_mismo_nombre()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(mediador.IdDevuelto);
        var alta = mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>().Subject;
        alta.TrabajadorId.Should().Be(_asignado.Id);
        alta.EmpresaId.Should().BeNull();
        alta.ClienteId.Should().BeNull();
        alta.TipoDocumentoId.Should().Be(_formacion.Id);
        alta.FechaEmision.Should().Be(_hoy.AddDays(-1));
        alta.FechaVencimientoManual.Should().Be(_hoy.AddMonths(12));
        alta.ArchivoUrl.Should().Be("clave-del-pdf");
        alta.Comentarios.Should().Be("Curso de 2026");
    }

    [Fact]
    public async Task La_Empresa_propia_incluida_en_la_autorizacion_recibe_el_documento_de_Empresa()
    {
        var requisito = Requisito("Seguro de responsabilidad civil", entidad: "Empresa");
        Autorizar(incluyeEmpresa: true);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _propia.Id, SujetoRequerimientoTec.Empresa), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var alta = mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>().Subject;
        alta.EmpresaId.Should().Be(_propia.Id);
        alta.TrabajadorId.Should().BeNull();
        alta.TipoDocumentoId.Should().Be(_seguro.Id);
    }

    [Fact]
    public async Task Con_un_documento_vigente_propio_del_tipo_lo_renueva_con_su_version()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var vigente = Documento.DeTrabajador(_asignado.Id, _formacion.Id, _hoy.AddMonths(-2), VigenciaDocumento.VenceEl(_hoy.AddMonths(1)));
        _documentos.ListaDocumentos.Add(vigente);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var renovacion = mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<RenovarDocumentoCommand>().Subject;
        renovacion.Id.Should().Be(vigente.Id);
        renovacion.Version.Should().Be(vigente.Version);
        renovacion.ArchivoUrl.Should().Be("clave-del-pdf");
        renovacion.FechaEmision.Should().Be(_hoy.AddDays(-1));
    }

    [Fact]
    public async Task Un_documento_vencido_no_es_vigente_y_se_crea_uno_nuevo()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        _documentos.ListaDocumentos.Add(Documento.DeTrabajador(
            _asignado.Id, _formacion.Id, _hoy.AddYears(-2), VigenciaDocumento.VenceEl(_hoy.AddDays(-1))));
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>();
    }

    [Fact]
    public async Task Un_sujeto_no_asignado_al_contrato_de_acceso_se_rechaza_sin_escribir()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _noAsignado.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.SujetoNoAsignado.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task La_Empresa_propia_sin_la_autorizacion_de_sus_documentos_no_es_sujeto()
    {
        var requisito = Requisito("Seguro de responsabilidad civil", entidad: "Empresa");
        Autorizar(incluyeEmpresa: false, _asignado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _propia.Id, SujetoRequerimientoTec.Empresa), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.SujetoNoAsignado.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_de_otro_Tenant_TEC_no_es_sujeto_aunque_se_nombre_su_Id()
    {
        var requisito = Requisito("Formación en altura");
        // Asignado en una autorización, pero el filtro de Tenant no lo deja ver: es de otro Tenant TEC.
        var ajeno = Trabajador.DeEmpresa(Guid.NewGuid(), "Pedro", "Sanz", "22222222J");
        Autorizar(trabajadores: [_asignado, ajeno]);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, ajeno.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.SujetoNoAsignado.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_contrato_de_acceso_de_otro_Tenant_TEC_no_sale_de_X2_y_se_rechaza()
    {
        Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();
        var ajeno = new SubirDocumentoRequerimientoTecCommand(Guid.NewGuid(), _requisitos[0].RequisitoId,
            SujetoRequerimientoTec.Trabajador, _asignado.Id, _hoy, null, "clave-del-pdf", null);

        var resultado = await handler.Handle(ajeno, CancellationToken.None);

        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.RequisitoNoEncontrado.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_requisito_que_no_esta_en_X2_se_rechaza()
    {
        Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();
        var inventado = new SubirDocumentoRequerimientoTecCommand(_contrato, Guid.NewGuid(),
            SujetoRequerimientoTec.Trabajador, _asignado.Id, _hoy, null, "clave-del-pdf", null);

        var resultado = await handler.Handle(inventado, CancellationToken.None);

        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.RequisitoNoEncontrado.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_requisito_de_documento_especifico_del_Tenant_TCE_no_se_cubre_con_uno_general()
    {
        var requisito = Requisito("Formación en altura", ambito: "EspecificoTce");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.RequisitoEspecifico.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_ParticipanteTec_se_deniega_antes_de_leer_nada()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, lector) = Montar(puerta: new PuertaQueNiega());

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionInboundErrores.SinCapacidad.Codigo);
        lector.Llamadas.Should().Be(0);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_el_rol_Administrador_se_deniega()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, lector) = Montar(rol: "GestorCae");

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(AutorizacionLecturaAutoridad.SoloAdministrador.Codigo);
        // El rol se comprueba antes de leer X-2: sin él no se consulta nada.
        lector.Llamadas.Should().Be(0);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task El_certificado_de_aptitud_de_solo_conclusion_es_de_salud_pero_si_se_sube()
    {
        var requisito = Requisito("Certificado de aptitud médica");
        Autorizar(trabajadores: _asignado);
        var aptitud = new TipoDocumento("Certificado de aptitud médica", 12, false, 5, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
        aptitud.EstablecerSoloConclusionAptitud(true);
        _tipos.ListaTiposDocumento.Add(aptitud);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>()
            .Which.TipoDocumentoId.Should().Be(aptitud.Id);
    }

    [Fact]
    public async Task Un_tipo_archivado_del_mismo_nombre_no_cuenta_y_se_usa_el_activo()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var archivado = new TipoDocumento("Formación en altura", null, false, 6, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.DatosPersonales);
        archivado.Archivar(null);
        _tipos.ListaTiposDocumento.Add(archivado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>()
            .Which.TipoDocumentoId.Should().Be(_formacion.Id);
    }

    [Fact]
    public async Task Si_el_unico_tipo_del_nombre_esta_archivado_falla_sin_crear_con_el()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        _formacion.Archivar(null);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("RequerimientoTec.TipoSinCorrespondencia");
        // El nombre es único por Tenant propietario y un tipo archivado no se desarchiva: no se ofrece crearlo.
        resultado.Error.Mensaje.Should().Contain("archivado").And.NotContain("créalo");
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tipo_del_mismo_nombre_pero_de_otro_ambito_no_cuenta()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        _tipos.ListaTiposDocumento.Add(new TipoDocumento("Formación en altura", null, false, 7, AmbitoAplicacion.Empresa,
            sensibilidad: SensibilidadDocumental.SinDatosPersonales));
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>()
            .Which.TipoDocumentoId.Should().Be(_formacion.Id);
    }

    [Fact]
    public async Task El_mismo_requisito_en_dos_contratos_de_acceso_se_toma_del_contrato_que_se_nombra()
    {
        var requisito = Requisito("Formación en altura");
        // El mismo requisito en otro contrato de acceso, delante y con un ámbito que no se puede subir: si el contrato no
        // contara, se tomaría esta fila y se rechazaría.
        _requisitos.Insert(0, requisito with { ContratoAccesoId = Guid.NewGuid(), AmbitoDocumento = "EspecificoTce" });
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>();
    }

    [Fact]
    public async Task Sin_un_tipo_propio_del_mismo_nombre_exacto_falla_sin_buscar_por_parecido()
    {
        var requisito = Requisito("formación en altura");
        Autorizar(trabajadores: _asignado);
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("RequerimientoTec.TipoSinCorrespondencia");
        resultado.Error.Mensaje.Should().Contain("créalo con ese nombre exacto");
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_dos_tipos_propios_del_mismo_nombre_falla_en_vez_de_elegir_uno()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        _tipos.ListaTiposDocumento.Add(new TipoDocumento("Formación en altura", null, false, 3, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.DatosPersonales));
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("RequerimientoTec.TipoSinCorrespondencia");
        resultado.Error.Mensaje.Should().Contain("2 tipos");
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tipo_de_salud_que_el_Tenant_TCE_no_puede_leer_no_se_sube()
    {
        var requisito = Requisito("Informe médico");
        Autorizar(trabajadores: _asignado);
        _tipos.ListaTiposDocumento.Add(new TipoDocumento("Informe médico", null, false, 4, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud));
        var (handler, mediador, _) = Montar();

        var resultado = await handler.Handle(Subida(requisito, _asignado.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(SubirDocumentoRequerimientoTecErrores.TipoDeSaludNoLegible.Codigo);
        mediador.Enviados.Should().BeEmpty();
    }

    /// <summary>Pasa por la guarda de servicio real (sin el marcador, DN-2) y después por el handler.</summary>
    private static Task<Result<Guid>> PorLaGuarda(
        SubirDocumentoRequerimientoTecCommandHandler handler, SubirDocumentoRequerimientoTecCommand comando, ICurrentUserService usuario) =>
        new GuardaPorServicioBehavior<SubirDocumentoRequerimientoTecCommand, Result<Guid>>(usuario)
            .Handle(comando, _ => handler.Handle(comando, CancellationToken.None), CancellationToken.None);

    [Fact]
    public async Task El_Administrador_del_Operador_CAE_externo_con_papel_Outbound_y_ParticipanteTec_sube_el_documento()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var usuario = new UsuarioConPapel(PapelActivo.Outbound);
        var (handler, mediador, _) = Montar(usuarioActual: usuario);

        var resultado = await PorLaGuarda(handler, Subida(requisito, _asignado.Id), usuario);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
        mediador.Enviados.Should().ContainSingle().Which.Should().BeOfType<CrearDocumentoCommand>()
            .Which.TrabajadorId.Should().Be(_asignado.Id);
    }

    [Fact]
    public async Task Con_papel_Outbound_pero_sin_ser_participante_del_Tenant_TEC_se_deniega_sin_leer_ni_escribir()
    {
        var requisito = Requisito("Formación en altura");
        Autorizar(trabajadores: _asignado);
        var usuario = new UsuarioConPapel(PapelActivo.Outbound);
        var (handler, mediador, lector) = Montar(puerta: new PuertaQueNiega(), usuarioActual: usuario);

        var resultado = await PorLaGuarda(handler, Subida(requisito, _asignado.Id), usuario);

        resultado.Error.Codigo.Should().Be(AutorizacionInboundErrores.SinCapacidad.Codigo);
        lector.Llamadas.Should().Be(0);
        mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public void El_comando_es_del_lado_del_Tenant_TEC_sin_marcador_Inbound_y_su_error_no_nombra_a_ningun_Gestor_CAE()
    {
        // DN-2, como sus cuatro hermanos de ParticipanteTec: con el marcador, la guarda negaría el papel Outbound.
        typeof(IComandoInbound).IsAssignableFrom(typeof(SubirDocumentoRequerimientoTecCommand)).Should().BeFalse();
        foreach (var error in new[]
                 {
                     SubirDocumentoRequerimientoTecErrores.RequisitoNoEncontrado, SubirDocumentoRequerimientoTecErrores.RequisitoEspecifico,
                     SubirDocumentoRequerimientoTecErrores.SujetoNoAsignado, SubirDocumentoRequerimientoTecErrores.TipoDeSaludNoLegible,
                 })
        {
            error.Mensaje.Should().NotContain("Gestor");
            // Quien lee no es «el Cliente»: es la Empresa que pide el documento.
            error.Mensaje.Should().NotContain("Cliente");
        }
    }
}
