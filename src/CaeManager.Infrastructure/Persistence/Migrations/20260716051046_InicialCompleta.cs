using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InicialCompleta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alertas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nivel = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FechaGeneracionUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alertas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Asignaciones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaAlta = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FechaBaja = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asignaciones", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NombreCompleto = table.Column<string>(type: "TEXT", nullable: false),
                    Tema = table.Column<int>(type: "INTEGER", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Centros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CodigoCentro = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Direccion = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Contacto = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ContratoVigenteHasta = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Centros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RazonSocial = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Cif = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    EsCritico = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notas = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CredencialesAccesoEmpresa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UrlAcceso = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CampoEmpresa = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Usuario = table.Column<string>(type: "TEXT", nullable: true),
                    Contrasena = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesAccesoEmpresa", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Documentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FechaEmision = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FechaVencimiento = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ArchivoUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Comentarios = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documentos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Empresas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RazonSocial = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Empresas", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmpresasClientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClienteId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmpresasClientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ParametrosSistema",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UmbralAmbarDias = table.Column<int>(type: "INTEGER", nullable: false),
                    UmbralRojoDias = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParametrosSistema", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlataformasAcceso",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NombrePlataforma = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    UrlAcceso = table.Column<string>(type: "TEXT", nullable: true),
                    Usuario = table.Column<string>(type: "TEXT", nullable: true),
                    Contrasena = table.Column<string>(type: "TEXT", nullable: true),
                    Notas = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlataformasAcceso", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosAuditoria",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntidadTipo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    EntidadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Accion = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DatosAntes = table.Column<string>(type: "TEXT", nullable: true),
                    DatosDespues = table.Column<string>(type: "TEXT", nullable: true),
                    UsuarioId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosAuditoria", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequisitosDocumentales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    PeriodicidadEspecial = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    BloqueaAcceso = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notas = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitosDocumentales", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TiposDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    VigenciaMeses = table.Column<int>(type: "INTEGER", nullable: true),
                    AplicaVencimientoAutomatico = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notas = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Orden = table.Column<int>(type: "INTEGER", nullable: false),
                    Descripcion = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CriteriosValidacion = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SeSolicitaA = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Observaciones = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiposDocumento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TiposDocumentoCentros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CentroId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TiposDocumentoCentros", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trabajadores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Nombre = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Apellidos = table.Column<string>(type: "TEXT", maxLength: 150, nullable: false),
                    Dni = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FechaNacimiento = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Observaciones = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "INTEGER", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trabajadores", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("30000000-0000-0000-0000-000000000001"), "30000000-0000-0000-0000-000000000001", "Administrador", "ADMINISTRADOR" },
                    { new Guid("30000000-0000-0000-0000-000000000002"), "30000000-0000-0000-0000-000000000002", "Supervisor", "SUPERVISOR" },
                    { new Guid("30000000-0000-0000-0000-000000000003"), "30000000-0000-0000-0000-000000000003", "EjecutivoCae", "EJECUTIVOCAE" },
                    { new Guid("30000000-0000-0000-0000-000000000004"), "30000000-0000-0000-0000-000000000004", "Consulta", "CONSULTA" }
                });

            migrationBuilder.InsertData(
                table: "ParametrosSistema",
                columns: new[] { "Id", "UmbralAmbarDias", "UmbralRojoDias" },
                values: new object[] { new Guid("20000000-0000-0000-0000-000000000001"), 30, 15 });

            migrationBuilder.InsertData(
                table: "TiposDocumento",
                columns: new[] { "Id", "AplicaVencimientoAutomatico", "CriteriosValidacion", "Descripcion", "Nombre", "Notas", "Observaciones", "Orden", "SeSolicitaA", "VigenciaMeses" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), true, null, null, "Apto médico laboral", "Renovación anual estándar.", null, 1, null, 12 },
                    { new Guid("10000000-0000-0000-0000-000000000002"), true, null, null, "EPIS (firma)", "Se firman cada año según nota de origen.", null, 2, null, 12 },
                    { new Guid("10000000-0000-0000-0000-000000000003"), true, null, null, "Reciclaje 4h", "Cada 4 años, según Dpto. Formación.", null, 3, null, 48 },
                    { new Guid("10000000-0000-0000-0000-000000000004"), true, null, null, "Formación Art. 19", "Recordatorio cada 3 años.", null, 4, null, 36 },
                    { new Guid("10000000-0000-0000-0000-000000000005"), false, null, null, "Formación 60h (base convenio)", "Formación base, no consta caducidad.", null, 5, null, null },
                    { new Guid("10000000-0000-0000-0000-000000000006"), false, null, null, "Formación 20h", "Mismo curso de convenio que 60h/6h, no consta caducidad.", null, 6, null, null },
                    { new Guid("10000000-0000-0000-0000-000000000007"), false, null, null, "Formación 6h", "Mismo curso de convenio que 60h/20h, no consta caducidad.", null, 7, null, null },
                    { new Guid("10000000-0000-0000-0000-000000000008"), false, null, null, "Información Art. 18", "No consta periodicidad de renovación.", null, 8, null, null },
                    { new Guid("10000000-0000-0000-0000-000000000009"), false, null, null, "Carretillas elevadoras", "Configurable si el convenio interno define vigencia.", null, 9, null, null },
                    { new Guid("10000000-0000-0000-0000-00000000000a"), false, null, null, "PEMP (plataformas elevadoras)", "Configurable si el convenio interno define vigencia.", null, 10, null, null },
                    { new Guid("10000000-0000-0000-0000-00000000000b"), false, null, null, "LOTO (4h)", "Configurable si el convenio interno define vigencia.", null, 11, null, null },
                    { new Guid("10000000-0000-0000-0000-00000000000c"), false, null, null, "Seguridad alimentaria", "Configurable si el convenio interno define vigencia.", null, 12, null, null },
                    { new Guid("10000000-0000-0000-0000-00000000000d"), false, null, null, "Primeros auxilios", "Se recomienda revisar cada 2 años; sin dato oficial de origen.", null, 13, null, null },
                    { new Guid("10000000-0000-0000-0000-00000000000e"), false, null, null, "Espacios confinados", "Configurable si el convenio interno define vigencia.", null, 14, null, null },
                    { new Guid("10000000-0000-0000-0000-00000000000f"), false, null, null, "Trabajos en altura (8h)", "Configurable si el convenio interno define vigencia.", null, 15, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_DocumentoId",
                table: "Alertas",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_CentroId",
                table: "Asignaciones",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_Asignaciones_TrabajadorId_CentroId_FechaAlta",
                table: "Asignaciones",
                columns: new[] { "TrabajadorId", "CentroId", "FechaAlta" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Centros_ClienteId",
                table: "Centros",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_Centros_EmpresaId",
                table: "Centros",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_Cif",
                table: "Clientes",
                column: "Cif",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_RazonSocial",
                table: "Clientes",
                column: "RazonSocial");

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAccesoEmpresa_EmpresaId",
                table: "CredencialesAccesoEmpresa",
                column: "EmpresaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_FechaVencimiento",
                table: "Documentos",
                column: "FechaVencimiento");

            migrationBuilder.CreateIndex(
                name: "IX_Documentos_TrabajadorId_TipoDocumentoId",
                table: "Documentos",
                columns: new[] { "TrabajadorId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_Empresas_RazonSocial",
                table: "Empresas",
                column: "RazonSocial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_ClienteId",
                table: "EmpresasClientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_EmpresaId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "EmpresaId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlataformasAcceso_CentroId",
                table: "PlataformasAcceso",
                column: "CentroId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_EntidadTipo_EntidadId",
                table: "RegistrosAuditoria",
                columns: new[] { "EntidadTipo", "EntidadId" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosAuditoria_FechaUtc",
                table: "RegistrosAuditoria",
                column: "FechaUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RequisitosDocumentales_CentroId",
                table: "RequisitosDocumentales",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumento_Nombre",
                table: "TiposDocumento",
                column: "Nombre",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_CentroId",
                table: "TiposDocumentoCentros",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_TiposDocumentoCentros_TipoDocumentoId_CentroId",
                table: "TiposDocumentoCentros",
                columns: new[] { "TipoDocumentoId", "CentroId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_Dni",
                table: "Trabajadores",
                column: "Dni",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_EmpresaId",
                table: "Trabajadores",
                column: "EmpresaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alertas");

            migrationBuilder.DropTable(
                name: "Asignaciones");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "Centros");

            migrationBuilder.DropTable(
                name: "Clientes");

            migrationBuilder.DropTable(
                name: "CredencialesAccesoEmpresa");

            migrationBuilder.DropTable(
                name: "Documentos");

            migrationBuilder.DropTable(
                name: "Empresas");

            migrationBuilder.DropTable(
                name: "EmpresasClientes");

            migrationBuilder.DropTable(
                name: "ParametrosSistema");

            migrationBuilder.DropTable(
                name: "PlataformasAcceso");

            migrationBuilder.DropTable(
                name: "RegistrosAuditoria");

            migrationBuilder.DropTable(
                name: "RequisitosDocumentales");

            migrationBuilder.DropTable(
                name: "TiposDocumento");

            migrationBuilder.DropTable(
                name: "TiposDocumentoCentros");

            migrationBuilder.DropTable(
                name: "Trabajadores");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
