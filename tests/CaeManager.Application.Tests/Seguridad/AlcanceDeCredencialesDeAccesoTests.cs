using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.GuardarCredencialAccesoSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Seguridad;

/// <summary>
/// Las credenciales de acceso al portal de un cliente —URL, usuario y
/// contraseña— son las filas más sensibles del tenant, y sus cuatro
/// operaciones eran las únicas de sus agregados que no comprobaban la cartera.
///
/// <para>
/// Es el fallo del Issue #18 —un Gestor CAE podía leer cualquier fila fuera de
/// su cartera con solo conocer el Guid— sobre el dato que más importa: once
/// operaciones de Subcontrata sí lo comprobaban, y la que escribía credenciales
/// no. Se destapó auditando por mutación el ratchet que debía impedirlo: su
/// filtro solo reconocía comandos cuyo identificador se llamara <c>Id</c>, así
/// que un comando identificado por <c>SubcontrataId</c> no llegaba siquiera a
/// evaluarse.
/// </para>
///
/// <para>
/// La denegación devuelve "no encontrado" y no un error de autorización, por la
/// convención que documenta <c>AlcanceDatosServiceExtensions</c>: un error
/// explícito confirmaría a quien no debe verla que la fila existe.
/// </para>
/// </summary>
public class AlcanceDeCredencialesDeAccesoTests
{
    [Fact]
    public async Task Fuera_de_cartera_no_se_pueden_sobreescribir_las_credenciales_de_una_subcontrata()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata ajena S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var credenciales = new CredencialSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoSubcontrataCommandHandler(
            new EmpresaRepositorioFalso(subcontrata),
            credenciales,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: []),
            unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoSubcontrataCommand(
                subcontrata.Id, "https://portal.example", "Contrata", "usuario", "secreta"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Subcontrata.NoEncontrada",
            "una denegación por cartera no puede distinguirse de una fila inexistente");
        credenciales.Agregadas.Should().Be(0, "no debe escribirse nada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Dentro_de_cartera_las_credenciales_de_una_subcontrata_se_guardan()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata propia S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var credenciales = new CredencialSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoSubcontrataCommandHandler(
            new EmpresaRepositorioFalso(subcontrata),
            credenciales,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id]),
            unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoSubcontrataCommand(
                subcontrata.Id, "https://portal.example", "Contrata", "usuario", "secreta"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la comprobación acota, no bloquea");
        credenciales.Agregadas.Should().Be(1);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Fuera_de_cartera_no_se_pueden_sobreescribir_las_credenciales_de_una_empresa()
    {
        var empresa = new Empresa("Empresa ajena S.L.");
        var credenciales = new CredencialEmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoEmpresaCommandHandler(
            new EmpresaRepositorioFalso(empresa),
            credenciales,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []),
            unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoEmpresaCommand(
                empresa.Id, "https://portal.example", "Empresa", "usuario", "secreta"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        credenciales.Agregadas.Should().Be(0);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Dentro_de_cartera_las_credenciales_de_una_empresa_se_guardan()
    {
        var empresa = new Empresa("Empresa propia S.L.");
        var credenciales = new CredencialEmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoEmpresaCommandHandler(
            new EmpresaRepositorioFalso(empresa),
            credenciales,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]),
            unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoEmpresaCommand(
                empresa.Id, "https://portal.example", "Empresa", "usuario", "secreta"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credenciales.Agregadas.Should().Be(1);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    // En los dos tests de lectura el contexto de datos que se inyecta EXPLOTA si
    // alguien lo toca: la propiedad que importa no es solo "devuelve null", es
    // que la consulta ni siquiera llega a la tabla de credenciales cuando el
    // agregado está fuera de la cartera.

    [Fact]
    public async Task Fuera_de_cartera_no_se_leen_las_credenciales_de_una_subcontrata()
    {
        var handler = new ObtenerCredencialAccesoSubcontrataQueryHandler(
            new SubcontratasQueryContextQueExplota(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: []));

        var resultado = await handler.Handle(
            new ObtenerCredencialAccesoSubcontrataQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Fuera_de_cartera_no_se_leen_las_credenciales_de_una_empresa()
    {
        var handler = new ObtenerCredencialAccesoEmpresaQueryHandler(
            new EmpresasQueryContextQueExplota(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []));

        var resultado = await handler.Handle(
            new ObtenerCredencialAccesoEmpresaQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
    }

    private sealed class EmpresaRepositorioFalso(Empresa empresa) : IEmpresaRepository
    {
        public Task<Empresa?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Empresa?>(empresa);

        public Task<bool> ExisteConRazonSocialAsync(
            string razonSocial, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> ExisteConCifAsync(
            string cif, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TieneTrabajadoresAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TieneCentrosComoTitularAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<bool> TieneTrabajadoresComoSubcontrataAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public void Agregar(Empresa nueva) { }
    }

    private sealed class CredencialSubcontrataRepositorioFalso : ICredencialAccesoSubcontrataRepository
    {
        public int Agregadas { get; private set; }

        public Task<CredencialAccesoSubcontrata?> ObtenerPorSubcontrataAsync(
            Guid subcontrataId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CredencialAccesoSubcontrata?>(null);

        public void Agregar(CredencialAccesoSubcontrata credencial) => Agregadas++;
    }

    private sealed class CredencialEmpresaRepositorioFalso : ICredencialAccesoEmpresaRepository
    {
        public int Agregadas { get; private set; }

        public Task<CredencialAccesoEmpresa?> ObtenerPorEmpresaAsync(
            Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CredencialAccesoEmpresa?>(null);

        public void Agregar(CredencialAccesoEmpresa credencial) => Agregadas++;
    }

    private sealed class SubcontratasQueryContextQueExplota : ISubcontratasQueryContext
    {
        private static IQueryable<T> Explota<T>() =>
            throw new InvalidOperationException(
                "La consulta llegó a la base de datos con el agregado fuera de la cartera del usuario.");

        public IQueryable<Subcontrata> Subcontratas => Explota<Subcontrata>();



        public IQueryable<CredencialAccesoSubcontrata> CredencialesAccesoSubcontrata =>
            Explota<CredencialAccesoSubcontrata>();

        public IQueryable<VerificacionExternaSubcontrata> VerificacionesExternaSubcontrata =>
            Explota<VerificacionExternaSubcontrata>();
    }

    private sealed class EmpresasQueryContextQueExplota : IEmpresasQueryContext
    {
        private static IQueryable<T> Explota<T>() =>
            throw new InvalidOperationException(
                "La consulta llegó a la base de datos con el agregado fuera de la cartera del usuario.");

        public IQueryable<Empresa> Empresas => Explota<Empresa>();


        public IQueryable<CredencialAccesoEmpresa> CredencialesAccesoEmpresa => Explota<CredencialAccesoEmpresa>();

        public IQueryable<RelacionEmpresarial> RelacionesEmpresariales => Explota<RelacionEmpresarial>();
    }
}
