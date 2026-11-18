"""Bounded ordinary-client combat actions. No server administration commands."""
import time


class MatchFinished(Exception):
    """Exact observed match archive finalized; return to receipt validation."""


class Combat:
    def __init__(self, clients, serials, rpc, remaining, report, finished=lambda:False):
        self.clients, self.serials, self.rpc, self.remaining = clients, serials, rpc, remaining
        self.report = report
        self.finished = finished
        report['combat_actions'] = []

    def act(self, seat, action):
        if self.finished():
            raise MatchFinished()
        self.report['combat_actions'].append({'seat':seat, 'action':action})
        return self.rpc(self.clients[seat], {'cmd':'act', 'action':action})

    def observe(self, seat):
        return self.rpc(self.clients[seat], {'cmd':'observe', 'terrain_radius':0})['obs']

    def wait(self, seconds):
        until = time.monotonic()+seconds
        while time.monotonic()<until:
            if self.finished():
                raise MatchFinished()
            self.remaining()
            for client in self.clients:
                self.rpc(client, {'cmd':'pump', 'ms':100})
            time.sleep(.1)

    def say(self, seat, text):
        return self.act(seat, {'type':'Say', 'text':text})

    def inventory(self, seat):
        obs = self.observe(seat)
        bags = [i for i in obs['items'] if i.get('layer') == 21 and i.get('container') == self.serials[seat]]
        if not bags:
            raise ValueError('backpack not observed')
        bag = bags[0]['serial']
        self.act(seat, {'type':'Use', 'serial':bag});self.wait(.4)
        return [i for i in self.observe(seat)['items'] if i.get('container') == bag]

    def potion(self, seat, graphic):
        items = [i for i in self.inventory(seat) if i['graphic']==graphic]
        if not items:
            raise ValueError('required potion missing '+str(graphic))
        before = self.observe(seat)['player']
        self.act(seat, {'type':'Use', 'serial':items[0]['serial']});self.wait(.6)
        after = self.observe(seat)['player']
        return {'before':before, 'after':after}

    def prepare(self):
        for seat in range(2):
            self.say(seat, '[Arena enter');self.wait(.5)
            self.say(seat, '[Arena quick 7');self.wait(.5)
            obs = self.observe(seat)
            gumps = [g for g in obs['gumps'] if 'QUICK PREPARATION' in str(g)]
            if len(gumps)!=1:
                raise ValueError('quick preparation gump missing')
            g = gumps[0]
            self.act(seat, {'type':'GumpResponse','serial':g['serial'],'gump_id':g['gump_id'],'button':1})
            self.wait(.5)
            player = self.observe(seat)['player']
            if [player['strength'],player['dexterity'],player['intelligence']] != [100,25,100]:
                raise ValueError('quick preparation stats incorrect')
        # Strength creates a real temporary stat modifier and a real empty bottle.
        self.report['strength_potion'] = self.potion(0, 0xF09)
        if self.report['strength_potion']['after']['strength'] <= 100:
            raise ValueError('strength potion failed')
        before = self.inventory(0)
        bottles = [i['serial'] for i in before if i['graphic']==0xF0E]
        if not bottles:
            raise ValueError('consumed potion did not produce an empty bottle')
        self.say(0, '[Arena supplies');self.wait(.5)
        after = self.inventory(0)
        if any(i['serial'] in bottles for i in after):
            raise ValueError('refill did not remove empty bottles from backpack')
        self.report['bottle_refill'] = {'empty_bottle_serials':bottles,'removed_from_backpack':True,
            'strength_potions_after':sum(i.get('amount',1) for i in after if i['graphic']==0xF09),
            'bank_destination':'not directly observed over client protocol'}

    def cast(self, seat, spell, target):
        before = [self.observe(s)['player'] for s in range(2)]
        self.act(seat, {'type':'CastSpell','spell':spell})
        # Wait for the actual cursor, not a guessed cast delay.
        for _ in range(45):
            self.wait(.1)
            if self.observe(seat).get('pending_target'):
                self.act(seat, {'type':'TargetObject','serial':target});break
        else:
            self.report['failed_cast_observation'] = self.observe(seat)
            raise ValueError('spell produced no target cursor: '+str(spell))
        self.wait(.5)
        after = [self.observe(s)['player'] for s in range(2)]
        entry = {'seat':seat,'spell':spell,'before':before,'after':after}
        self.report.setdefault('casts',[]).append(entry)
        return entry

    # UO direction codes by (sign dx, sign dy); north is -y.
    DIRECTIONS = {(0,-1):0, (1,-1):1, (1,0):2, (1,1):3, (0,1):4, (-1,1):5, (-1,0):6, (-1,-1):7}

    def approach(self, target, distance=4, spell_range=10, seconds=10, max_steps=30):
        """Close distance with single manual steps; never use bridge WalkTo.

        WalkTo queues a bridge route that every later pump re-paths. Live
        reruns showed that re-pathing made pumps take seconds (one tile per
        2-5 s), stalled a pump past the 10 s response deadline mid-fight, and,
        if still active when the shard released the fighter outside the arena,
        stalled the post-match pump. A manual Walk sends one step and cancels
        any route. Fails closed if the attacker never reaches spell range."""
        until = time.monotonic()+seconds
        steps = 0
        while True:
            pos = self.observe(0)['player']['pos']
            dx, dy = target['x']-pos['x'], target['y']-pos['y']
            gap = max(abs(dx), abs(dy))
            if gap <= distance or steps >= max_steps or time.monotonic() >= until:
                break
            sign = lambda v: (v > 0) - (v < 0)
            self.act(0, {'type':'Walk', 'dir':self.DIRECTIONS[(sign(dx), sign(dy))], 'run':True})
            steps += 1
            self.wait(.25)
        self.report['approach'] = {'target':[target['x'],target['y']], 'final':[pos['x'],pos['y']], 'gap':gap, 'steps':steps}
        if gap > spell_range:
            raise ValueError('attacker did not reach spell range')

    def fight(self):
        player = self.observe(0)['player']
        if player['strength'] != 100:
            raise ValueError('round start did not clear strength modifier')
        self.report['round_reset_strength'] = player['strength']
        # Bring attacker into normal spell range; defender remains stationary.
        self.approach(self.observe(1)['player']['pos'])
        self.cast(0, 42, self.serials[1])
        self.cast(0, 42, self.serials[1])
        heal = self.potion(1, 0xF0C)
        self.report['heal_potion'] = heal
        if heal['after']['hits'] <= heal['before']['hits']:
            raise ValueError('heal potion did not heal')
        self.cast(0, 42, self.serials[1])
        healing = self.cast(1, 29, self.serials[1])
        if healing['after'][1]['hits'] <= healing['before'][1]['hits']:
            raise ValueError('greater heal did not heal')
        for _ in range(10):
            if self.observe(1)['player'].get('dead'):
                return
            if self.observe(0)['player']['mana'] < 20:
                self.act(0, {'type':'WarMode','on':False})
                self.act(0, {'type':'UseSkill','skill':46})
                for _ in range(20):
                    self.wait(1)
                    if self.observe(0)['player']['mana'] >= 20:
                        break
            self.cast(0,42,self.serials[1])
