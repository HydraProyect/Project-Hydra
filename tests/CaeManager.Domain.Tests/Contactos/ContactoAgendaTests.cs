using CaeManager.Domain.Contactos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Contactos;

public class ContactoAgendaTests
{
    private static ContactoAgenda CrearContacto() =>
        ContactoAgenda.DeEmpresa(Guid.NewGuid(), "Juan Pérez", "juan@example.com");

    [Fact]
    public void Nace_sin_roles()
    {
        var contacto = CrearContacto();

        contacto.Roles.Should().BeEmpty();
    }

    [Fact]
    public void EstablecerRoles_asigna_varios_roles_a_la_vez()
    {
        var contacto = CrearContacto();

        contacto.EstablecerRoles([RolContacto.ResponsablePrl, RolContacto.ContactoCae]);

        contacto.Roles.Should().HaveCount(2);
        contacto.Roles.Select(r => r.Rol).Should().BeEquivalentTo([RolContacto.ResponsablePrl, RolContacto.ContactoCae]);
    }

    [Fact]
    public void EstablecerRoles_ignora_duplicados()
    {
        var contacto = CrearContacto();

        contacto.EstablecerRoles([RolContacto.ResponsablePrl, RolContacto.ResponsablePrl]);

        contacto.Roles.Should().ContainSingle();
    }

    [Fact]
    public void EstablecerRoles_reemplaza_el_conjunto_completo_en_vez_de_acumular()
    {
        var contacto = CrearContacto();
        contacto.EstablecerRoles([RolContacto.ResponsablePrl, RolContacto.RepresentanteLegal]);

        contacto.EstablecerRoles([RolContacto.ContactoCae]);

        contacto.Roles.Should().ContainSingle(r => r.Rol == RolContacto.ContactoCae);
    }

    [Fact]
    public void EstablecerRoles_con_lista_vacia_deja_el_contacto_sin_roles()
    {
        var contacto = CrearContacto();
        contacto.EstablecerRoles([RolContacto.ResponsablePrl]);

        contacto.EstablecerRoles([]);

        contacto.Roles.Should().BeEmpty();
    }
}
