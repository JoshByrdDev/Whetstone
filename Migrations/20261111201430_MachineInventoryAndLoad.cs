using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Whetstone.Migrations
{
    /// <inheritdoc />
    public partial class MachineInventoryAndLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MachineInventories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Place = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false),
                    CpuModel = table.Column<string>(type: "TEXT", nullable: true),
                    PhysicalCores = table.Column<int>(type: "INTEGER", nullable: true),
                    LogicalCores = table.Column<int>(type: "INTEGER", nullable: true),
                    PerfCores = table.Column<int>(type: "INTEGER", nullable: true),
                    EffCores = table.Column<int>(type: "INTEGER", nullable: true),
                    RamMb = table.Column<long>(type: "INTEGER", nullable: true),
                    Os = table.Column<string>(type: "TEXT", nullable: true),
                    OsVersion = table.Column<string>(type: "TEXT", nullable: true),
                    Arch = table.Column<string>(type: "TEXT", nullable: true),
                    DiskKind = table.Column<string>(type: "TEXT", nullable: true),
                    DiskTotalGb = table.Column<double>(type: "REAL", nullable: true),
                    DockerCpus = table.Column<int>(type: "INTEGER", nullable: true),
                    DockerMemMb = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineInventories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MachineLoadSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Place = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CpuPct = table.Column<double>(type: "REAL", nullable: true),
                    MemAvailMb = table.Column<long>(type: "INTEGER", nullable: true),
                    MemTotalMb = table.Column<long>(type: "INTEGER", nullable: true),
                    SwapUsedMb = table.Column<long>(type: "INTEGER", nullable: true),
                    Pressure = table.Column<int>(type: "INTEGER", nullable: true),
                    DiskFreeGb = table.Column<double>(type: "REAL", nullable: true),
                    NetInKbps = table.Column<double>(type: "REAL", nullable: true),
                    NetOutKbps = table.Column<double>(type: "REAL", nullable: true),
                    DockerMemUsedMb = table.Column<long>(type: "INTEGER", nullable: true),
                    DockerMemLimitMb = table.Column<long>(type: "INTEGER", nullable: true),
                    Busy = table.Column<int>(type: "INTEGER", nullable: false),
                    PhasesJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MachineLoadSamples", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MachineInventories_Place_LastSeen",
                table: "MachineInventories",
                columns: new[] { "Place", "LastSeen" });

            migrationBuilder.CreateIndex(
                name: "IX_MachineLoadSamples_At",
                table: "MachineLoadSamples",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_MachineLoadSamples_Place_At",
                table: "MachineLoadSamples",
                columns: new[] { "Place", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MachineInventories");

            migrationBuilder.DropTable(
                name: "MachineLoadSamples");
        }
    }
}
