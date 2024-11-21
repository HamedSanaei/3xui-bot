﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace Adminbot.Migrations
{
    [DbContext(typeof(UserDbContext))]
    [Migration("20240412204428_zibaltable")]
    partial class zibaltable
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "7.0.0");

            modelBuilder.Entity("Adminbot.Domain.ZibalPaymentInfo", b =>
                {
                    b.Property<string>("TrackId")
                        .HasColumnType("TEXT");

                    b.Property<long>("Amount")
                        .HasColumnType("INTEGER");

                    b.Property<string>("CallbackUrl")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsAddedToBallance")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Result")
                        .HasColumnType("TEXT");

                    b.Property<long>("TelMsgId")
                        .HasColumnType("INTEGER");

                    b.Property<long>("TelegramUserId")
                        .HasColumnType("INTEGER");

                    b.HasKey("TrackId");

                    b.ToTable("ZibalPaymentInfos");
                });

            modelBuilder.Entity("CookieData", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("TEXT");

                    b.Property<DateTimeOffset>("ExpirationDate")
                        .HasColumnType("TEXT");

                    b.Property<string>("SessionCookie")
                        .HasColumnType("TEXT");

                    b.Property<string>("Url")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Cookies");
                });

            modelBuilder.Entity("User", b =>
                {
                    b.Property<long>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("AccountCounter")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ConfigLink")
                        .HasColumnType("TEXT");

                    b.Property<long>("ConfigPrice")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Email")
                        .HasColumnType("TEXT");

                    b.Property<string>("Flow")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastFreeAcc")
                        .HasColumnType("TEXT");

                    b.Property<string>("LastStep")
                        .HasColumnType("TEXT");

                    b.Property<string>("PaymentMethod")
                        .HasColumnType("TEXT");

                    b.Property<string>("SelectedCountry")
                        .HasColumnType("TEXT");

                    b.Property<string>("SelectedPeriod")
                        .HasColumnType("TEXT");

                    b.Property<string>("SubLink")
                        .HasColumnType("TEXT");

                    b.Property<string>("TotoalGB")
                        .HasColumnType("TEXT");

                    b.Property<string>("Type")
                        .HasColumnType("TEXT");

                    b.Property<string>("_ConfigPrice")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Users");
                });
#pragma warning restore 612, 618
        }
    }
}
