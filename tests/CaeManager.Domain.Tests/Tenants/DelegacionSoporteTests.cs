using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Tenants;

/// <summary>
/// Acceso de soporte de Hydra al tenant de un cliente (decisión del
/// propietario del producto, 2026-07-31).
///
/// El modelo es "provisionado pero apagado": la delegación se crea al dar de
/// alta el tenant para no tener que montar nada cuando entra una queja, pero
/// nace inactiva y abrirla exige motivo y ventana. Así, si la cuenta de
/// soporte se ve comprometida no abre ninguna puerta por sí sola, y ante un
/// cliente se puede sostener "entramos del día X al Y por el motivo Z".
/// </summary>
public class DelegacionSoporteTests
{
    private static readonly Guid Plataforma = Guid.NewGuid();
    private static readonly Guid Cliente = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Una_delegacion_de_soporte_nace_apagada()
    {
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);

        delegacion.Proposito.Should().Be(PropositoDelegacion.Soporte);
        delegacion.Activa.Should().BeFalse("estar provisionada no es tener acceso");
        delegacion.EstaVigente(Ahora).Should().BeFalse();
    }

    [Fact]
    public void Una_delegacion_comercial_nace_activa_y_sin_caducidad()
    {
        // Contrapeso: el cambio no puede alterar el comportamiento de las
        // delegaciones de negocio que ya existían.
        var delegacion = new DelegacionTenant(Plataforma, Cliente);

        delegacion.Proposito.Should().Be(PropositoDelegacion.Comercial);
        delegacion.Activa.Should().BeTrue();
        delegacion.EstaVigente(Ahora).Should().BeTrue();
        delegacion.ExpiraEnUtc.Should().BeNull();
    }

    [Fact]
    public void Abrir_el_acceso_exige_motivo()
    {
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);

        var activar = () => delegacion.ActivarParaSoporte("  ", Ahora.AddDays(2), Ahora);

        activar.Should().Throw<ArgumentException>().WithMessage("*por qué*");
    }

    [Fact]
    public void Abrir_el_acceso_exige_una_ventana_que_termine_despues_de_empezar()
    {
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);

        var activar = () => delegacion.ActivarParaSoporte("Incidencia #442", Ahora.AddHours(-1), Ahora);

        activar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Abierta_con_motivo_y_ventana_queda_todo_registrado()
    {
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);
        var expira = Ahora.AddDays(2);

        delegacion.ActivarParaSoporte("Incidencia #442: no carga el listado de documentos", expira, Ahora);

        delegacion.Activa.Should().BeTrue();
        delegacion.MotivoActivacion.Should().Contain("Incidencia #442");
        delegacion.ExpiraEnUtc.Should().Be(expira);
        delegacion.ActivadaEnUtc.Should().Be(Ahora);
        delegacion.EstaVigente(Ahora).Should().BeTrue();
    }

    [Fact]
    public void Pasada_la_ventana_deja_de_valer_sin_que_nadie_la_toque()
    {
        // La caducidad se comprueba en el dominio y no solo en la consulta que
        // lo lee, para que ninguna vía de acceso pueda saltársela.
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);
        var expira = Ahora.AddDays(2);
        delegacion.ActivarParaSoporte("Incidencia #442", expira, Ahora);

        delegacion.EstaVigente(expira.AddSeconds(1)).Should().BeFalse();
        delegacion.Activa.Should().BeTrue("el flag sigue puesto: lo que caducó es la ventana");
    }

    /// <summary>
    /// DEC-43: reabrir mientras la ventana anterior sigue viva movería
    /// <see cref="DelegacionTenant.ExpiraEnUtc"/> hacia delante desde el
    /// instante de la segunda llamada — una prórroga silenciosa. Repetir la
    /// apertura cada pocas horas encadenaría un acceso indefinido sin que
    /// ninguna llamada individual superase el techo de la vía de plataforma.
    /// </summary>
    [Fact]
    public void Reabrir_una_ventana_todavia_vigente_se_rechaza_y_no_mueve_la_expiracion()
    {
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);
        var expiraOriginal = Ahora.AddHours(4);
        delegacion.ActivarParaSoporte("Incidencia #442", expiraOriginal, Ahora);

        var reabrir = () => delegacion.ActivarParaSoporte("Incidencia #443", Ahora.AddHours(3).AddHours(4), Ahora.AddHours(3));

        reabrir.Should().Throw<InvalidOperationException>("la ventana de la incidencia #442 seguía viva");
        delegacion.ExpiraEnUtc.Should().Be(expiraOriginal, "el intento rechazado no puede haber movido la expiración");
        delegacion.MotivoActivacion.Should().Contain("#442", "el motivo original tampoco cambia con un intento rechazado");
    }

    [Fact]
    public void Reabrir_despues_de_caducada_es_una_activacion_nueva_no_una_extension()
    {
        // Distinto del test de arriba: aquí la ventana anterior ya venció.
        // Volver a abrir es un acceso nuevo con su propio motivo, no una
        // prórroga del anterior — DEC-43 no lo prohíbe.
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);
        delegacion.ActivarParaSoporte("Incidencia #442", Ahora.AddHours(4), Ahora);

        var masTarde = Ahora.AddHours(5);
        var nuevaExpira = masTarde.AddHours(4);
        delegacion.ActivarParaSoporte("Incidencia #443", nuevaExpira, masTarde);

        delegacion.ExpiraEnUtc.Should().Be(nuevaExpira);
        delegacion.MotivoActivacion.Should().Contain("#443");
        delegacion.EstaVigente(masTarde).Should().BeTrue();
    }

    [Fact]
    public void Cerrar_el_acceso_borra_la_ventana_y_el_motivo()
    {
        var delegacion = DelegacionTenant.ParaSoporte(Plataforma, Cliente);
        delegacion.ActivarParaSoporte("Incidencia #442", Ahora.AddDays(2), Ahora);

        delegacion.Desactivar();

        delegacion.EstaVigente(Ahora).Should().BeFalse();
        delegacion.ExpiraEnUtc.Should().BeNull();
        delegacion.MotivoActivacion.Should().BeNull("una ventana cerrada no conserva el motivo de la anterior");
    }

    [Fact]
    public void Una_delegacion_comercial_no_se_activa_por_la_via_de_soporte()
    {
        // Son relaciones jurídicamente distintas: una comercial no debe
        // acabar con motivo y caducidad de soporte por descuido.
        var delegacion = new DelegacionTenant(Plataforma, Cliente);

        var activar = () => delegacion.ActivarParaSoporte("Incidencia", Ahora.AddDays(1), Ahora);

        activar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void El_tenant_de_plataforma_se_marca_explicitamente()
    {
        // Y no se deduce del Id …0001, que es público y determinista.
        new Tenant("Hydra", esPlataforma: true).EsPlataforma.Should().BeTrue();
        new Tenant("Cliente cualquiera").EsPlataforma.Should().BeFalse();
    }
}
