"""Regenerate every UI/FX/font/icon deliverable + mockups: python3 build_all.py"""
import gen_fonts, build_ui, gen_fx, gen_icon, mockups, specimen
gen_fonts.main(); build_ui.main(); gen_fx.main(); gen_icon.main(); specimen.main(); mockups.main()
