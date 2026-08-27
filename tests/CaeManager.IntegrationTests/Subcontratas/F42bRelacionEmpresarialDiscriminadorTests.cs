using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Subcontratas;

/// <summary>
/// F4.2b (2026-08-27): <c>ObtenerSubcontrataPorIdQuery</c> y
/// <c>ObtenerSubcontratasDeClienteQuery</c> repuntados de las tablas puente
/// legacy a <c>RelacionesEmpresariales</c>. A diferencia de otros lectores
/// migrados, aquí el JOIN discriminador contra <c>Empresa.EsPropia</c>/
/// <c>EsCritico</c>/<c>NivelServicio</c> no es defensa en profundidad: una
/// Subcontrata con Clientes Y Empresas propias a la vez, o un Cliente con
/// una Empresa propia Y una Subcontrata sirviéndole a la vez, son la
/// situación normal en cualquier tenant con actividad real — no un caso
/// límite bajo un rol privilegiado. Sin el discriminador, estos tests
/// fallarían con el primer par de datos realistas, no solo bajo ataque.
/// Verificado por una revisión adversarial independiente antes de
/// implementar (convergencia pre-cliente, 2026-08-27) antes de escribir
/// estos tests.
/// </summary>
public class F42bRelacionEmpresarialDiscriminadorTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task ObtenerSubcontratasDeClienteQuery_no_incluye_la_Empresa_propia_que_tambien_sirve_al_mismo_Cliente()
    {
        Guid clienteId, subcontrataId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Con Ambos Proveedores S.A.", "B10380210", false, null, null);
            var subcontrata = Empresa.CrearComoSubcontrata("La Unica Subcontrata S.L.", "B10380228", NivelServicioSubcontrata.Supervisada.ToString());
            var empresaPropia = new Empresa("Empresa Propia Que Tambien Sirve S.L.", "B10380236");
            contexto.Empresas.AddRange(cliente, subcontrata, empresaPropia);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; subcontrataId = subcontrata.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            // Shape Subcontrata→Cliente (la que SÍ debe aparecer) y Empresa→Cliente
            // (una Empresa propia sirviendo al mismo Cliente — NO debe colarse).
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(empresaPropiaId, clienteId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontratasDeClienteQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontratasDeClienteQuery(clienteId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(subcontrataId);
    }

    /// <summary>
    /// Eje de vigencia con <b>control positivo</b>: la revisión adversarial
    /// señaló que un test que solo siembra una relación cerrada y afirma
    /// "vacío" no distingue "el filtro de vigencia funciona" de "la consulta
    /// no devuelve nada nunca" — y esa segunda posibilidad es justo la clase
    /// de fallo por sobre-restricción que hay que poder ver. Por eso aquí
    /// conviven una vigente y una cerrada, y se afirma la identidad de la que
    /// sobrevive, no solo la ausencia de la otra.
    /// </summary>
    [Fact]
    public async Task ObtenerSubcontratasDeClienteQuery_ignora_la_relacion_cerrada_y_conserva_la_vigente()
    {
        Guid clienteId, subcontrataVigenteId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Con Una Baja S.A.", "B10380251", false, null, null);
            var vigente = Empresa.CrearComoSubcontrata("Subcontrata Aun Vigente S.L.", "B10380244", NivelServicioSubcontrata.Gestionada.ToString());
            var cerrada = Empresa.CrearComoSubcontrata("Subcontrata Ya Cerrada S.L.", "B10380186", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(cliente, vigente, cerrada);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; subcontrataVigenteId = vigente.Id;

            var ahora = DateTime.UtcNow;
            var relacionCerrada = RelacionEmpresarial.Migrar(cerrada.Id, clienteId, ahora.AddMonths(-6), ahora);
            relacionCerrada.Cerrar(ahora);
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataVigenteId, clienteId, ahora, ahora),
                relacionCerrada);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontratasDeClienteQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontratasDeClienteQuery(clienteId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(subcontrataVigenteId);
    }

    /// <summary>
    /// <c>ObtenerSupervisionSubcontrataQuery</c> era el lector que más cambió
    /// (reordenación de JOINs y eliminación de uno) y el único que se quedó
    /// sin instrumento — lo señaló la revisión adversarial. Cubre sus dos
    /// ejes a la vez: solo aparecen centros de Clientes reales (no de
    /// Empresas propias servidas) y solo de relaciones vigentes.
    /// </summary>
    [Fact]
    public async Task ObtenerSupervisionSubcontrataQuery_solo_ofrece_centros_de_Clientes_reales_con_relacion_vigente()
    {
        Guid subcontrataId, centroDelClienteVigenteId;
        await using (var contexto = CrearContexto())
        {
            contexto.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));

            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Supervisada S.L.", "B10380392", NivelServicioSubcontrata.Supervisada.ToString());
            var clienteVigente = Empresa.CrearComoCliente("Cliente Vigente Con Centro S.A.", "B10380400", false, null, null);
            var clienteCerrado = Empresa.CrearComoCliente("Cliente Ya Desvinculado S.A.", "B10380418", false, null, null);
            var empresaPropia = new Empresa("Empresa Propia Servida Tambien S.L.", "B10380426");
            var ejecutora = new Empresa("Empresa Ejecutora Del Centro S.L.", "B10380434");
            contexto.Empresas.AddRange(subcontrata, clienteVigente, clienteCerrado, empresaPropia, ejecutora);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id;

            var centroVigente = new Centro(clienteVigente.Id, ejecutora.Id, "Centro Del Cliente Vigente");
            var centroCerrado = new Centro(clienteCerrado.Id, ejecutora.Id, "Centro Del Cliente Cerrado");
            var centroDeEmpresaPropia = new Centro(empresaPropia.Id, ejecutora.Id, "Centro De La Empresa Propia");
            contexto.Centros.AddRange(centroVigente, centroCerrado, centroDeEmpresaPropia);

            var ahora = DateTime.UtcNow;
            var relacionCerrada = RelacionEmpresarial.Migrar(subcontrataId, clienteCerrado.Id, ahora.AddMonths(-6), ahora);
            relacionCerrada.Cerrar(ahora);
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteVigente.Id, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrataId, empresaPropia.Id, ahora, ahora),
                relacionCerrada);
            await contexto.SaveChangesAsync();
            centroDelClienteVigenteId = centroVigente.Id;
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSupervisionSubcontrataQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSupervisionSubcontrataQuery(subcontrataId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.CentrosSeleccionables.Should().ContainSingle()
            .Which.CentroId.Should().Be(centroDelClienteVigenteId);
    }

    /// <summary>
    /// El cambio de comportamiento real de este lector no es el discriminador
    /// (la consulta base ya filtra <c>NivelServicio != null</c>) sino el filtro
    /// de vigencia: la tabla legacy <c>SubcontratasEmpresas</c> borraba
    /// físicamente al desvincular, así que "sin fila" y "relación cerrada"
    /// eran indistinguibles. Con la arista unificada, una subcontrata que dejó
    /// de prestar servicio sigue teniendo su fila — sin el filtro, el selector
    /// seguiría ofreciéndola.
    /// </summary>
    [Fact]
    public async Task ObtenerSubcontratasParaSelectorQuery_no_ofrece_una_subcontrata_cuya_relacion_ya_esta_cerrada()
    {
        Guid empresaPropiaId, subcontrataVigenteId;
        await using (var contexto = CrearContexto())
        {
            var empresaPropia = new Empresa("Empresa Propia Del Selector S.L.", "B10380269");
            var vigente = Empresa.CrearComoSubcontrata("Subcontrata Todavia Activa S.L.", "B10380277", NivelServicioSubcontrata.Gestionada.ToString());
            var cerrada = Empresa.CrearComoSubcontrata("Subcontrata Ya Desvinculada S.L.", "B10380285", NivelServicioSubcontrata.Gestionada.ToString());
            var sinRelacion = Empresa.CrearComoSubcontrata("Subcontrata Sin Vinculo S.L.", "B10380293", NivelServicioSubcontrata.Supervisada.ToString());
            contexto.Empresas.AddRange(empresaPropia, vigente, cerrada, sinRelacion);
            await contexto.SaveChangesAsync();
            empresaPropiaId = empresaPropia.Id; subcontrataVigenteId = vigente.Id;

            var ahora = DateTime.UtcNow;
            var relacionCerrada = RelacionEmpresarial.Migrar(cerrada.Id, empresaPropiaId, ahora.AddMonths(-3), ahora);
            relacionCerrada.Cerrar(ahora);
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataVigenteId, empresaPropiaId, ahora, ahora),
                relacionCerrada);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontratasParaSelectorQueryHandler(lectura);
        var resultado = await handler.Handle(new ObtenerSubcontratasParaSelectorQuery(empresaPropiaId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(subcontrataVigenteId);
    }

    /// <summary>
    /// Espejo de <c>ObtenerSubcontratasDeCliente</c>: el mismo Cliente servido
    /// a la vez por una Empresa propia y por una Subcontrata — situación
    /// corriente, no caso límite. La pestaña "Empresas" solo debe mostrar la
    /// Empresa propia.
    /// </summary>
    [Fact]
    public async Task ObtenerEmpresasDeClienteQuery_no_incluye_la_Subcontrata_que_tambien_sirve_al_mismo_Cliente()
    {
        Guid clienteId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Con Los Dos Proveedores S.A.", "B10380301", false, null, null);
            var empresaPropia = new Empresa("La Empresa Propia Correcta S.L.", "B10380319");
            var subcontrata = Empresa.CrearComoSubcontrata("La Subcontrata Que No Toca S.L.", "B10380327", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(cliente, empresaPropia, subcontrata);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(empresaPropiaId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrata.Id, clienteId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerEmpresasDeClienteQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerEmpresasDeClienteQuery(clienteId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(empresaPropiaId);
    }

    /// <summary>
    /// Dirección contraria: el lado fijado es la proveedora y el ambiguo la
    /// contraparte. Una Subcontrata que presta servicio tanto a un Cliente
    /// real como a una Empresa propia solo debe listar el Cliente.
    /// </summary>
    [Fact]
    public async Task ObtenerClientesDeEmpresaQuery_no_incluye_la_Empresa_propia_a_la_que_esa_Subcontrata_sirve()
    {
        Guid subcontrataId, clienteRealId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata De Doble Cara S.L.", "B10380335", NivelServicioSubcontrata.Gestionada.ToString());
            var clienteReal = Empresa.CrearComoCliente("El Cliente Real Que Toca S.A.", "B10380343", false, null, null);
            var empresaPropia = new Empresa("Empresa Propia Que No Es Cliente S.L.", "B10380350");
            contexto.Empresas.AddRange(subcontrata, clienteReal, empresaPropia);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id; clienteRealId = clienteReal.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteRealId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrataId, empresaPropia.Id, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerClientesDeEmpresaQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerClientesDeEmpresaQuery(subcontrataId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(clienteRealId);
    }

    /// <summary>
    /// Dos propiedades a la vez, porque el defecto tenía dos capas: sin
    /// <c>ClienteId</c> el selector ofrecía TODA la tabla <c>Empresas</c>
    /// (incluidas contrapartes, defecto heredado de F3a), y con
    /// <c>ClienteId</c> podía colar una Subcontrata que sirviera al mismo
    /// Cliente.
    /// </summary>
    [Fact]
    public async Task ObtenerEmpresasParaSelectorQuery_solo_ofrece_Empresas_propias_con_y_sin_ClienteId()
    {
        Guid clienteId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Del Selector De Empresas S.A.", "B10380368", false, null, null);
            var empresaPropia = new Empresa("Empresa Propia Ofrecible S.L.", "B10380376");
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata No Ofrecible S.L.", "B10380384", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(cliente, empresaPropia, subcontrata);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(empresaPropiaId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrata.Id, clienteId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerEmpresasParaSelectorQueryHandler(lectura);

        var acotado = await handler.Handle(new ObtenerEmpresasParaSelectorQuery(clienteId), CancellationToken.None);
        acotado.Should().ContainSingle().Which.Id.Should().Be(empresaPropiaId);

        // Sin ClienteId: el catálogo global tampoco debe incluir contrapartes.
        var completo = await handler.Handle(new ObtenerEmpresasParaSelectorQuery(), CancellationToken.None);
        completo.Should().ContainSingle().Which.Id.Should().Be(empresaPropiaId);
    }

    /// <summary>
    /// F4.2c re-migra este lector (su primera migración, en F4.2b, se
    /// revirtió por el fallo de pérdida de datos). El DTO de detalle separa
    /// los dos ejes por la fila real de la contraparte: <c>ClienteIds</c>
    /// solo Clientes (<c>EsCritico != null</c>), <c>EmpresaIds</c> solo
    /// Empresas propias — aunque ambas shapes compartan la columna
    /// <c>RelacionEmpresarial.ClienteId</c>.
    /// </summary>
    [Fact]
    public async Task ObtenerSubcontrataPorIdQuery_separa_ClienteIds_de_EmpresaIds_por_la_fila_real()
    {
        Guid subcontrataId, clienteId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Con Dos Ejes S.L.", "B10380442", NivelServicioSubcontrata.Gestionada.ToString());
            var cliente = Empresa.CrearComoCliente("Cliente Del Eje Clientes S.A.", "B10380459", false, null, null);
            var empresaPropia = new Empresa("Empresa Del Eje Empresas S.L.", "B10380467");
            contexto.Empresas.AddRange(subcontrata, cliente, empresaPropia);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id; clienteId = cliente.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrataId, empresaPropiaId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontrataPorIdQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrataId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ClienteIds.Should().BeEquivalentTo([clienteId]);
        resultado.EmpresaIds.Should().BeEquivalentTo([empresaPropiaId]);
    }

    /// <summary>
    /// La mitad de lectura del invariante de opacas: una contraparte
    /// soft-deleted no aparece en NINGÚN eje del DTO — es exactamente lo que
    /// el usuario no puede desmarcar, y por eso el diff de escritura de
    /// <c>EditarSubcontrataCommand</c> tampoco la cuenta como "actual". El
    /// ciclo completo DTO→Editar se mide en
    /// <c>FuenteUnicaRelacionEmpresarialTests</c>.
    /// </summary>
    [Fact]
    public async Task ObtenerSubcontrataPorIdQuery_no_incluye_la_contraparte_soft_deleted_en_ningun_eje()
    {
        Guid subcontrataId, clienteVivoId, clienteEliminadoId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Con Baja Opaca S.L.", "B10380475", NivelServicioSubcontrata.Supervisada.ToString());
            var clienteVivo = Empresa.CrearComoCliente("Cliente Vivo Del Detalle S.A.", "B10380483", false, null, null);
            var clienteEliminado = Empresa.CrearComoCliente("Cliente Eliminado Del Detalle S.A.", "B10380491", false, null, null);
            contexto.Empresas.AddRange(subcontrata, clienteVivo, clienteEliminado);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id; clienteVivoId = clienteVivo.Id; clienteEliminadoId = clienteEliminado.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteVivoId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrataId, clienteEliminadoId, ahora, ahora));
            await contexto.SaveChangesAsync();

            clienteEliminado.MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontrataPorIdQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrataId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ClienteIds.Should().BeEquivalentTo([clienteVivoId],
            "la contraparte soft-deleted es opaca: no se pinta, y por eso su ausencia en un request posterior no puede leerse como baja");
        resultado.EmpresaIds.Should().BeEmpty();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
