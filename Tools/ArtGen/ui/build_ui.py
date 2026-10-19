"""Build every Art/UI sprite + ui.json.  Run: python3 build_ui.py"""
import os
from uikit import Registry, UI_DIR
import gen_ui_display as D
import gen_ui_widgets as Wd
import gen_ui_hud2 as H2


def main():
    os.makedirs(UI_DIR, exist_ok=True)
    R = Registry(UI_DIR, 'ui.json')
    # --- display sprites (pivot = centre unless noted)
    for ch in '321':
        R.add('cue_' + ch, D.make_cue_numeral(ch))
    R.add('cue_draw', D.make_cue_draw())
    b = D.make_cue_burst()
    R.add('cue_burst', b, pivot=[100, 100], partial_alpha=True, cell=[200, 200], frames=4, fps=12, loop=True)
    logo, _ = D.make_logo()
    R.add('logo', logo)
    R.add('banner_win', D.make_banner('YOU WIN', 'gold', dict(), 'swallow', 'stars'))
    R.add('banner_lose', D.make_banner('DEFEATED', 'steel', dict(face='shadowPurp', hi='char2', dark='ink2'), 'torn', None))
    R.add('get_ready', D.make_get_ready())
    rs = D.make_round_sign()
    R.add('round_sign', rs, pivot=[28, 0], inner=[4, 3, 48, 12])
    for k, v in D.make_stamps().items():
        R.add(k, v)
    # --- HUD widgets
    R.add('portrait_frame', H2.portrait_frame(), pivot=[0, 0], inner=[4, 4, 36, 36])
    R.add('portrait_backdrop', H2.portrait_backdrop(), pivot=[0, 0])
    R.add('name_plate', H2.name_plate(), pivot=[0, 0], border=[5, 5, 5, 5])
    R.add('hp_frame', H2.hp_frame(), pivot=[0, 0], inner=list(H2.HP_IN))
    R.add('hp_fill', H2.hp_strip('red1', 'uiRedHi', 'red0', 'red3', 'red2'), pivot=[0, 0])
    R.add('hp_ghost', H2.hp_strip('cream1', 'white', 'cream0', 'cream2', 'paintCreamD'), pivot=[0, 0])
    R.add('hp_fill_low', H2.hp_strip('sigOrange', 'sun1', 'sigYellow', 'red1', 'sigRed'), pivot=[0, 0])
    R.add('hud_badge', H2.hud_badge(), pivot=[76, 59], numeralWindow=list(H2.NUM_WIN), timerWindow=list(H2.TIMER_WIN))
    R.add('panel_wood', Wd.panel_wood(), pivot=[0, 0], border=[7, 7, 7, 7])
    R.add('panel_paper', Wd.panel_paper(), pivot=[0, 0], border=[7, 7, 7, 7])
    R.add('btn_large', Wd.button(32, 32, 'paintRed', 'red0', 'red2', 'paintRedD'), pivot=[0, 0],
          border=[8, 8, 8, 11], contentOffset=[0, -1])
    R.add('btn_large_pressed', Wd.button(32, 32, 'paintRed', 'red0', 'red2', 'paintRedD', pressed=True), pivot=[0, 0],
          border=[8, 8, 8, 11], contentOffset=[0, 1])
    R.add('btn_large_alt', Wd.button(32, 32, 'wood1', 'wood0', 'wood2', 'wood3'), pivot=[0, 0],
          border=[8, 8, 8, 11], contentOffset=[0, -1])
    R.add('btn_large_alt_pressed', Wd.button(32, 32, 'wood1', 'wood0', 'wood2', 'wood3', pressed=True), pivot=[0, 0],
          border=[8, 8, 8, 11], contentOffset=[0, 1])
    R.add('btn_icon', Wd.button(30, 30, 'wood1', 'wood0', 'wood2', 'wood3'), pivot=[0, 0], contentOffset=[0, -1])
    R.add('btn_icon_pressed', Wd.button(30, 30, 'wood1', 'wood0', 'wood2', 'wood3', pressed=True), pivot=[0, 0],
          contentOffset=[0, 1])
    R.add('seg_on', Wd.button(16, 20, 'brass1', 'brass0', 'brass2', 'brass3', pressed=True, D=2), pivot=[0, 0],
          border=[5, 5, 5, 5])
    R.add('seg_off', Wd.button(16, 20, 'wood2', 'wood1', 'wood3', 'wood4', D=2), pivot=[0, 0], border=[5, 5, 5, 6])
    for k, v in Wd.make_icons().items():
        R.add(k, v)
    R.add('toggle_on', Wd.toggle(True))
    R.add('toggle_off', Wd.toggle(False))
    R.add('tap_hint', Wd.tap_hint(), pivot=[8, 15], cell=[16, 16], frames=2, fps=3, loop=True)
    R.write()
    print(len(R.meta), 'UI sprites')


if __name__ == '__main__':
    main()
