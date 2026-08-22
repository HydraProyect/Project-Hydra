using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class EstadoBootstrapPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origen",
                table: "ConcesionesPrivilegio",
                type: "character varying(25)",
                maxLength: 25,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "EstadoBootstrapPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioRaizId = table.Column<Guid>(type: "uuid", nullable: false),
                    DesignadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Consumido = table.Column<bool>(type: "boolean", nullable: false),
                    ConsumidoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstadoBootstrapPlataforma", x => x.Id);
                    table.CheckConstraint("CK_EstadoBootstrapPlataforma_FilaUnica", "\"Id\" = 'b0075742-0000-4000-8000-000000000001'");
                });

            // ── RLS: cuarta categoría de tabla ────────────────────────────
            //
            // No encaja en ninguna de las tres que ADR-011 ya distingue:
            //
            //   tenantizada          RLS por tenant  (aislamiento_tenant)
            //   catálogo global      RLS por posición en la asignación
            //   catálogo de privilegio  RLS por usuario (privilegio_del_usuario)
            //   >> estado de bootstrap: fila del SISTEMA, no de un usuario
            //
            // Y por eso su política se escribe entera en vez de copiar la del
            // plano 3: aquí "UsuarioRaizId" no dice de quién es la fila, dice a
            // quién designó el despliegue.
            //
            // CON FORCE, para que el propietario de la tabla no sea un bypass
            // accidental. Eso obliga a que TODA escritura legítima tenga su
            // política — y las tiene, tres, cada una para un acto distinto.
            //
            // Las políticas se escriben sobre COORDENADAS DE SESIÓN, nunca sobre
            // current_user: meter 'current_user = postgres' convertiría una
            // circunstancia operacional del despliegue de hoy en semántica de
            // autorización, que es justo lo que ADR-011 prohíbe.
            //
            // LÍMITE CONOCIDO, el mismo de F2b-5: hoy todos los entornos
            // conectan como superusuario y un superusuario ignora RLS con FORCE
            // o sin él. Esto protege desde que se complete la rotación a
            // cae_app_runtime; hasta entonces la garantía efectiva es la de la
            // capa de aplicación y su ratchet.
            migrationBuilder.Sql(@"
ALTER TABLE ""EstadoBootstrapPlataforma"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""EstadoBootstrapPlataforma"" FORCE ROW LEVEL SECURITY;

-- LECTURA. Solo la identidad raíz ve la fila. Para cualquier otro usuario la
-- tabla está vacía, y eso es correcto de cara a él: no distingue 'no soy la
-- raíz' de 'no hay raíz fijada', y ninguna de las dos cosas le incumbe.
CREATE POLICY estado_bootstrap_lectura_de_la_raiz ON ""EstadoBootstrapPlataforma""
    FOR SELECT
    USING (""UsuarioRaizId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid);

-- DESIGNACIÓN. La escribe el arranque de la aplicación, que no es una sesión de
-- usuario: la ausencia de app.usuario_id ES el discriminante, y es una
-- coordenada del modelo, no una propiedad del rol de conexión.
CREATE POLICY estado_bootstrap_designacion_al_arrancar ON ""EstadoBootstrapPlataforma""
    FOR INSERT
    WITH CHECK (NULLIF(current_setting('app.usuario_id', true), '') IS NULL);

-- CONSUMO. Lo escribe la propia raíz desde su sesión, en el mismo SaveChanges
-- que crea la concesión fundacional. El WITH CHECK repite el predicado a
-- propósito: sin él, la fila podría actualizarse para apuntar a otro usuario
-- raíz y saldría del alcance de quien la está tocando.
CREATE POLICY estado_bootstrap_consumo_por_la_raiz ON ""EstadoBootstrapPlataforma""
    FOR UPDATE
    USING (""UsuarioRaizId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid)
    WITH CHECK (""UsuarioRaizId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid);

-- No hay política de DELETE, y es deliberado: sin política permisiva, ningún
-- rol sujeto a RLS puede borrar la fila. La monotonía del bootstrap no depende
-- solo del dominio.
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS estado_bootstrap_consumo_por_la_raiz ON ""EstadoBootstrapPlataforma"";
DROP POLICY IF EXISTS estado_bootstrap_designacion_al_arrancar ON ""EstadoBootstrapPlataforma"";
DROP POLICY IF EXISTS estado_bootstrap_lectura_de_la_raiz ON ""EstadoBootstrapPlataforma"";
");

            migrationBuilder.DropTable(
                name: "EstadoBootstrapPlataforma");

            migrationBuilder.DropColumn(
                name: "Origen",
                table: "ConcesionesPrivilegio");
        }
    }
}
